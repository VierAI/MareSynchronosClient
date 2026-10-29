using FFXIVClientStructs.FFXIV.Client.Game.Object;
using MareSynchronos.PlayerData;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Mediator;
using MareSynchronos.WebAPI.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace MareSynchronos.Services;

/// <summary>
/// 1.15.5.1217: keeps <see cref="PSyncCrowd"/> fresh (about once a second, framework thread, cheap):
/// not-drawn state, distance to you, lifting holds, and (only when enabled) the memory budget every ~3 s.
/// </summary>
public sealed class PSyncCrowdService : DisposableMediatorSubscriberBase, IHostedService
{
    private const long MiB = 1024L * 1024L;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly PSyncGpuMemory _gpu;
    private readonly Stopwatch _tick = Stopwatch.StartNew();
    private long _lastCrowdMs = -10_000;
    private long _lastBudgetMs = -10_000;
    private readonly Dictionary<string, DateTimeOffset> _lastBudgetChange = new(StringComparer.Ordinal);
    private bool _budgetWasEnabled;
    private bool _blockMigrateDone;

    /// <summary>
    /// Audit M2 / N1: older entries may be vanity IDs (1217 stored the raw text). Runs ONCE per session, as soon as the
    /// pair list is loaded. An entry is rewritten only when it is not any known pair's UID (exact, case-sensitive) and
    /// exactly one known pair has that exact vanity ID (case-sensitive). Everything else is left untouched.
    /// </summary>
    private bool MigrateBlockedVanityIdsOnce()
    {
        var all = _pairManager.DirectPairs.Concat(_pairManager.GroupPairs.Values.SelectMany(v => v))
            .GroupBy(p => p.UserData.UID, StringComparer.Ordinal).Select(g => g.First()).ToList();
        if (all.Count == 0) return false; // pairs not loaded yet; try again next tick
        var uids = all.Select(p => p.UserData.UID).ToHashSet(StringComparer.Ordinal);
        var list = VierAIConfigStore.Current.BlockedUIDs;
        bool changed = false;
        lock (list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (string.IsNullOrEmpty(entry) || uids.Contains(entry)) continue; // already a UID: never rewrite
                if (PSyncCrowd.LooksLikeUid(entry)) continue; // N1b: UID-shaped entries are never treated as vanity IDs
                var matches = all.Where(p => !string.IsNullOrEmpty(p.UserData.Alias) && string.Equals(p.UserData.Alias, entry, StringComparison.Ordinal)).ToList();
                if (matches.Count != 1) continue;
                Logger.LogInformation("PSync blocklist: converted a vanity ID entry to its UID");
                list[i] = matches[0].UserData.UID;
                changed = true;
            }
            if (changed)
            {
                var dedup = list.Distinct(StringComparer.Ordinal).ToList();
                list.Clear();
                list.AddRange(dedup);
            }
        }
        if (changed) VierAIConfigStore.MarkDirty();
        return true;
    }

    private readonly MareSynchronos.MareConfiguration.PlayerPerformanceConfigService _perfConfig;

    public PSyncCrowdService(ILogger<PSyncCrowdService> logger, MareMediator mediator, PairManager pairManager, DalamudUtilService dalamudUtil,
        MareSynchronos.MareConfiguration.PlayerPerformanceConfigService perfConfig)
        : base(logger, mediator)
    {
        _perfConfig = perfConfig;
        _pairManager = pairManager;
        _dalamudUtil = dalamudUtil;
        _gpu = new PSyncGpuMemory(logger);
        PSyncCrowd.VisiblePairsProvider = () => _pairManager.GetVisiblePairs();
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, _ => OnTick());
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _gpu.Dispose();
        return Task.CompletedTask;
    }

    private void OnTick()
    {
        try
        {
            var now = _tick.ElapsedMilliseconds;
            if (now - _lastCrowdMs < 1000) return;
            _lastCrowdMs = now;
            if (_dalamudUtil.IsZoning) return;

            var visible = _pairManager.GetVisiblePairs();
            UpdateDrawAndDistance(visible);
            if (!_blockMigrateDone) _blockMigrateDone = MigrateBlockedVanityIdsOnce();
            PSyncPerfWarning.TickChat(visible, _perfConfig.Current, Mediator);

            var cfg = VierAIConfigStore.Current;
            _gpu.SetWanted(cfg.MemoryBudgetEnabled && cfg.MemoryBudgetMode == 1);
            if (cfg.MemoryBudgetEnabled)
            {
                if (now - _lastBudgetMs >= 3000)
                {
                    _lastBudgetMs = now;
                    EvaluateBudget(visible, cfg);
                }
            }
            else if (_budgetWasEnabled || !PSyncCrowd.BudgetHeldSince.IsEmpty)
            {
                // Off means today's behaviour: release everything at once.
                foreach (var uid in PSyncCrowd.BudgetHeldSince.Keys.ToList()) { PSyncCrowd.BudgetHeldSince.TryRemove(uid, out _); PSyncCrowd.PendingReapply[uid] = 0; }
                PSyncCrowd.BudgetModeText = "Off";
                PSyncCrowd.BudgetCapBytes = -1;
                PSyncCrowd.BudgetUsedBytes = -1;
            }
            _budgetWasEnabled = cfg.MemoryBudgetEnabled;

            LiftHolds(visible);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "PSyncCrowd tick failed");
        }
    }

    private unsafe void UpdateDrawAndDistance(List<Pair> visible)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var localPtr = _dalamudUtil.GetPlayerPtr();
        System.Numerics.Vector3? local = localPtr == nint.Zero ? null : (System.Numerics.Vector3)((GameObject*)localPtr)->Position;
        foreach (var pair in visible)
        {
            var uid = pair.UserData.UID;
            seen.Add(uid);
            if (pair.ContendId == 0 || !_dalamudUtil.TryGetPlayerAddressAndIndexOnFrameworkByContentId(pair.ContendId, out var addr, out _) || addr == nint.Zero)
            {
                // Audit L2: no address = we can't tell, so don't keep an old "not drawn" that would defer tiers 3-4.
                PSyncCrowd.NotDrawnSince.TryRemove(uid, out _);
                continue;
            }
            var state = _dalamudUtil.GetCharacterDrawStateFromFramework(addr, requireRenderable: false);
            var notDrawn = state.Condition is DrawCondition.DrawObjectZero or DrawCondition.CharacterBaseZero or DrawCondition.ObjectZero;
            if (notDrawn) PSyncCrowd.NotDrawnSince.TryAdd(uid, DateTimeOffset.UtcNow);
            else PSyncCrowd.NotDrawnSince.TryRemove(uid, out _);

            if (local != null)
            {
                System.Numerics.Vector3 p = ((GameObject*)addr)->Position;
                PSyncCrowd.Distances[uid] = System.Numerics.Vector3.Distance(local.Value, p);
            }
        }
        foreach (var uid in PSyncCrowd.Distances.Keys.Concat(PSyncCrowd.NotDrawnSince.Keys).Distinct().ToList())
        {
            if (!seen.Contains(uid)) PSyncCrowd.Forget(uid);
        }
    }

    /// <summary>Reapply pairs whose cached application was held, once nothing holds them anymore.</summary>
    private void LiftHolds(List<Pair> visible)
    {
        if (PSyncCrowd.PendingReapply.IsEmpty) return;
        foreach (var pair in visible)
        {
            var uid = pair.UserData.UID;
            if (!PSyncCrowd.PendingReapply.ContainsKey(uid) || PSyncCrowd.HoldsApply(pair)) continue;
            PSyncCrowd.PendingReapply.TryRemove(uid, out _);
            Logger.LogDebug("PSync: hold lifted for {pair}, reapplying", pair.PairUIDName);
            pair.ReapplyCachedData(forced: true);
        }
        var visibleUids = visible.Select(p => p.UserData.UID).ToHashSet(StringComparer.Ordinal);
        foreach (var uid in PSyncCrowd.PendingReapply.Keys.ToList())
            if (!visibleUids.Contains(uid)) PSyncCrowd.PendingReapply.TryRemove(uid, out _);
    }

    /// <summary>
    /// Greedy fill in tier-then-distance order (same tiers as the download queue). Tier 0 always counts and is never held.
    /// The first pair that does not fit, and everything after it, is held, so the farthest tier-4 pair is held first and
    /// higher tiers are only touched once lower tiers are all held. Hysteresis: a held pair must fit under cap*(1-margin)
    /// to come back, and no pair changes state more often than the minimum hold time.
    /// </summary>
    private void EvaluateBudget(List<Pair> visible, VierAIConfig cfg)
    {
        var nearby = _dalamudUtil.NearbyPlayerCount;
        long manualCap = Math.Max(0, cfg.MemoryBudgetManualMiB) * MiB;
        bool crowded = cfg.MemoryBudgetCrowdedPlayers > 0 && nearby >= cfg.MemoryBudgetCrowdedPlayers;
        long crowdedCap = crowded && cfg.MemoryBudgetCrowdedMiB > 0 ? cfg.MemoryBudgetCrowdedMiB * MiB : long.MaxValue;

        var ordered = visible.Where(p => !p.IsPaused && !PSyncCrowd.IsBlocked(p))
            .Select(p => (Pair: p, Tier: PSyncCrowd.TierOf(p), Dist: PSyncCrowd.DistanceOf(p.UserData.UID), Cost: Math.Max(0, p.LastAppliedApproximateVRAMBytes)))
            .OrderBy(x => x.Tier).ThenBy(x => x.Dist).ToList();
        long syncedApplied = ordered.Where(x => !PSyncCrowd.IsBudgetHeld(x.Pair.UserData.UID)).Sum(x => x.Cost);

        long cap = manualCap;
        string mode = "Manual";
        if (cfg.MemoryBudgetMode == 1)
        {
            if (_gpu.TryGetCap(syncedApplied, cfg, out var autoCap, out var why))
            {
                cap = autoCap;
                mode = "Auto (GPU memory)";
            }
            else
            {
                mode = $"Manual (Auto unavailable: {why})";
            }
        }
        if (crowdedCap < cap)
        {
            cap = crowdedCap;
            mode += $", crowded ({nearby} players)";
        }
        PSyncCrowd.BudgetModeText = mode;
        PSyncCrowd.BudgetCapBytes = cap;

        double margin = Math.Clamp(cfg.MemoryBudgetMarginPercent, 0, 50) / 100.0;
        var minHold = TimeSpan.FromSeconds(Math.Max(10, cfg.MemoryBudgetMinHoldSeconds));
        var nowUtc = DateTimeOffset.UtcNow;
        long used = 0;
        bool exhausted = false;
        foreach (var x in ordered)
        {
            var uid = x.Pair.UserData.UID;
            bool held = PSyncCrowd.IsBudgetHeld(uid);
            bool allow;
            if (x.Tier == PriorityTiers.Listed || PSyncBudgetExempt.IsExempt(uid)) allow = true; // tier 0 and exempt players are never held
            else if (exhausted) allow = false;
            else
            {
                long limit = held ? (long)(cap * (1 - margin)) : cap;
                allow = used + x.Cost <= limit;
            }

            if (allow != !held && _lastBudgetChange.TryGetValue(uid, out var changed) && nowUtc - changed < minHold)
                allow = !held; // too soon to flip

            if (allow)
            {
                used += x.Cost;
                if (held)
                {
                    PSyncCrowd.BudgetHeldSince.TryRemove(uid, out _);
                    _lastBudgetChange[uid] = nowUtc;
                    Logger.LogInformation("PSync budget: resuming {pair} (tier {tier})", x.Pair.PairUIDName, x.Tier);
                    PSyncCrowd.PendingReapply[uid] = 0; // reapplied by LiftHolds; our data goes out as "newly visible"
                }
            }
            else
            {
                exhausted = true;
                if (!held)
                {
                    PSyncCrowd.BudgetHeldSince[uid] = nowUtc;
                    _lastBudgetChange[uid] = nowUtc;
                    Logger.LogInformation("PSync budget: holding {pair} (tier {tier}, {mb} MB, {dist:0} y)", x.Pair.PairUIDName, x.Tier, x.Cost / MiB, x.Dist);
                    if (x.Pair.HasCachedData)
                    {
                        var pair = x.Pair;
                        _ = Task.Run(() => pair.ReleaseAppliedDataAsync("memory budget"));
                    }
                }
            }
        }
        PSyncCrowd.BudgetUsedBytes = used;

        var visibleUids = visible.Select(p => p.UserData.UID).ToHashSet(StringComparer.Ordinal);
        foreach (var uid in PSyncCrowd.BudgetHeldSince.Keys.ToList())
            if (!visibleUids.Contains(uid)) PSyncCrowd.BudgetHeldSince.TryRemove(uid, out _);
    }
}
