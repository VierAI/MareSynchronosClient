using MareSynchronos.API.Data;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MareSynchronos.PlayerData.Pairs;

/// <summary>
/// 1.15.6.215 (VierAI): automatic fix for pairs drawn with missing body parts.
/// After an apply settles, the body, legs, head and face models are read (PSyncModelSlotCheck). If one is missing or failed to
/// load, the last data is reapplied with a redraw: first ~5 s after the apply, then once more 30 s after the first try if it is
/// still broken. At most 2 tries per pair per data version (data hash), so it can never loop. Stops on pause, hide, zone change,
/// disposal or when the setting (VierAIConfig.AutoHealMissingParts) is off.
/// Also keeps the list of files skipped for this pair because they are blocked (known bad), for the pair tooltip.
/// </summary>
public sealed partial class PairDataApplicationHandler
{
    private const int AutoHealMaxTries = 2;
    private static readonly TimeSpan AutoHealSettleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AutoHealFirstTryAt = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AutoHealSecondTryGap = TimeSpan.FromSeconds(30);

    private readonly object _psyncHealLock = new();
    private CancellationTokenSource? _psyncHealCts;
    private string? _psyncHealHash;
    private int _psyncHealTries;
    private DateTime _psyncHealLastTryUtc;
    // 1.15.6.216 (audit V1): set when the auto-fix asked for a reapply; the try is only counted when that apply really starts.
    private string? _psyncHealRequestedHash;
    // 1.15.6.217: the broken result a full heal round (2 tries) could not fix. Later data versions with the same result start no
    // new round (no repeated redraw cycles); cleared when the result changes, the pair looks complete, or on a zone change.
    private string? _psyncHealStuckDetail;
    // 1.15.6.217: slot bits (body, legs, face) the applied data has a model for; only those count as missing when empty.
    private int _psyncHealRequiredSlots;

    // Files skipped on the last validation because they are blocked: hash -> game path.
    private readonly ConcurrentDictionary<string, string> _psyncSkippedBad = new(StringComparer.OrdinalIgnoreCase);

    // Global apply activity, read by the compactor so new cache files are not compacted while pairs are loading.
    private static int s_psyncAppliesInFlight;
    private static long s_psyncLastApplyEndTicks;

    public static bool PSyncAnyApplyInFlight => Volatile.Read(ref s_psyncAppliesInFlight) > 0;
    public static TimeSpan PSyncSinceLastApply => TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - Interlocked.Read(ref s_psyncLastApplyEndTicks)));

    private static void PSyncGlobalApplyStart() => Interlocked.Increment(ref s_psyncAppliesInFlight);

    private static void PSyncGlobalApplyEnd()
    {
        Interlocked.Decrement(ref s_psyncAppliesInFlight);
        Interlocked.Exchange(ref s_psyncLastApplyEndTicks, DateTime.UtcNow.Ticks);
    }

    private static readonly System.Text.RegularExpressions.Regex s_bodyModelPath = new(@"^chara/human/c\d{4}/obj/body/b\d{4}/model/[^/]+\.mdl$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static bool PSyncIsBodyModelPath(string gamePath) => s_bodyModelPath.IsMatch(gamePath.Replace('\\', '/'));

    public IReadOnlyCollection<KeyValuePair<string, string>> PSyncGetSkippedBadFiles() => _psyncSkippedBad.ToArray();

    private void PSyncInitAutoHeal()
    {
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, _ =>
        {
            PSyncCancelAutoHeal();
            lock (_psyncHealLock) _psyncHealStuckDetail = null; // 217: a new zone gives a stuck pair one fresh round
        });
        Mediator.Subscribe<DalamudLogoutMessage>(this, _ => PSyncCancelAutoHeal());
    }

    private void PSyncCancelAutoHeal()
    {
        lock (_psyncHealLock)
        {
            try { _psyncHealCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            _psyncHealCts?.Dispose();
            _psyncHealCts = null;
            _psyncHealRequestedHash = null; // 217 (audit W1): a request that never started an apply is dropped
        }
    }

    /// <summary>
    /// 1.15.6.216 (audit V1): called when an apply starts. A newer apply cancels a pending auto-fix check (its own finish schedules a
    /// fresh one), and a reapply the auto-fix asked for counts as a try only now that it really runs. If the request was a no-op
    /// (another apply already running), nothing is counted.
    /// </summary>
    private void PSyncOnApplyStart(string? dataHash)
    {
        lock (_psyncHealLock)
        {
            try { _psyncHealCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            _psyncHealCts?.Dispose();
            _psyncHealCts = null;

            var requested = _psyncHealRequestedHash;
            if (requested == null) return;
            _psyncHealRequestedHash = null;
            if (!string.IsNullOrEmpty(dataHash) && string.Equals(requested, dataHash, StringComparison.Ordinal)
                && string.Equals(_psyncHealHash, dataHash, StringComparison.Ordinal))
            {
                _psyncHealTries++;
                _psyncHealLastTryUtc = DateTime.UtcNow;
            }
        }
    }

    private void PSyncNoteSkippedBad(string hash, string gamePath)
    {
        _psyncSkippedBad[hash] = gamePath;
        PlayerSync.FileCache.PSyncFileSafetyStore.NoteSkipped(hash, gamePath, Pair.UserData.UID, Pair.PlayerName ?? Pair.UserData.AliasOrUID);
    }

    /// <summary>Called at the end of every finished apply.</summary>
    private void PSyncScheduleAutoHeal(Guid applicationId, CharacterData charaData)
    {
        if (!VierAIConfigStore.Current.AutoHealMissingParts) return;
        var hash = charaData.DataHash.Value;
        if (string.IsNullOrEmpty(hash)) return;

        int tries;
        DateTime lastTry;
        CancellationToken token;
        lock (_psyncHealLock)
        {
            if (!string.Equals(_psyncHealHash, hash, StringComparison.Ordinal))
            {
                _psyncHealHash = hash;
                _psyncHealTries = 0;
                _psyncHealLastTryUtc = DateTime.MinValue;
                _psyncHealRequestedHash = null; // 217 (audit W1)
            }
            _psyncHealRequiredSlots = charaData.FileReplacements.TryGetValue(MareSynchronos.API.Data.Enum.ObjectKind.Player, out var playerFiles)
                ? PSyncModelSlotCheck.SlotsFromGamePaths(playerFiles.SelectMany(f => f.GamePaths))
                : 0;
            tries = _psyncHealTries;
            lastTry = _psyncHealLastTryUtc;
            try { _psyncHealCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            _psyncHealCts?.Dispose();
            _psyncHealCts = new CancellationTokenSource();
            token = _psyncHealCts.Token;
        }

        var appliedAt = DateTime.UtcNow;
        _ = Task.Run(() => PSyncAutoHealAsync(applicationId, hash, tries, lastTry, appliedAt, token), CancellationToken.None);
    }

    private bool PSyncAutoHealShouldStop(CancellationToken token)
        => token.IsCancellationRequested || !VierAIConfigStore.Current.AutoHealMissingParts || _dalamudUtil.IsZoning
           || Pair.IsPaused || !Pair.IsVisible || _charaHandler == null;

    private Task<(PSyncModelSlotCheck.SlotState State, string Detail)> PSyncCheckSlotsAsync()
        => _dalamudUtil.RunOnFrameworkThread(() =>
        {
            var handler = _charaHandler;
            var address = handler?.Address ?? nint.Zero;
            if (address == nint.Zero || !_dalamudUtil.IsLiveObject(address)) return (PSyncModelSlotCheck.SlotState.NoDrawObject, string.Empty);
            var state = PSyncModelSlotCheck.Check(address, Volatile.Read(ref _psyncHealRequiredSlots), out var detail);
            return (state, detail);
        });

    private async Task PSyncAutoHealAsync(Guid applicationId, string hash, int tries, DateTime lastTry, DateTime appliedAt, CancellationToken token)
    {
        try
        {
            await Task.Delay(AutoHealSettleDelay, token).ConfigureAwait(false);

            // Wait (bounded) for the game to finish loading the models.
            var result = (State: PSyncModelSlotCheck.SlotState.Loading, Detail: string.Empty);
            for (var i = 0; i < 12; i++)
            {
                if (PSyncAutoHealShouldStop(token)) return;
                result = await PSyncCheckSlotsAsync().ConfigureAwait(false);
                if (result.State != PSyncModelSlotCheck.SlotState.Loading) break;
                await Task.Delay(1000, token).ConfigureAwait(false);
            }

            if (result.State != PSyncModelSlotCheck.SlotState.Missing)
            {
                if (result.State == PSyncModelSlotCheck.SlotState.Ok) lock (_psyncHealLock) _psyncHealStuckDetail = null;
                if (tries > 0 && result.State == PSyncModelSlotCheck.SlotState.Ok)
                    Logger.LogInformation("[{applicationId}] PSync auto-fix: {pair} looks complete after {tries} reapply(s)", applicationId, Pair.PairUIDName, tries);
                return;
            }

            if (tries == 0)
            {
                lock (_psyncHealLock)
                {
                    if (string.Equals(_psyncHealStuckDetail, result.Detail, StringComparison.Ordinal))
                    {
                        Logger.LogDebug("[{applicationId}] PSync auto-fix: {pair} has the same missing part ({detail}) a full round could not fix; not reapplying again until it changes or you change zone",
                            applicationId, Pair.PairUIDName, result.Detail);
                        return;
                    }
                    _psyncHealStuckDetail = null;
                }
            }

            if (tries >= AutoHealMaxTries)
            {
                lock (_psyncHealLock) _psyncHealStuckDetail = result.Detail; // 217: no new rounds for the same result
                Logger.LogWarning("[{applicationId}] PSync auto-fix: {pair} still has missing parts ({detail}) after {max} reapplies; not trying again for this data. Use Reapply Last Data or Force Re-download.",
                    applicationId, Pair.PairUIDName, result.Detail, AutoHealMaxTries);
                return;
            }

            // First try ~5 s after the apply, the second 30 s after the first try.
            var due = tries == 0 ? appliedAt + AutoHealFirstTryAt : lastTry + AutoHealSecondTryGap;
            var wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            if (PSyncAutoHealShouldStop(token)) return;

            // Check again right before acting: it may have fixed itself (a late load) in the meantime.
            result = await PSyncCheckSlotsAsync().ConfigureAwait(false);
            if (result.State != PSyncModelSlotCheck.SlotState.Missing) return;

            lock (_psyncHealLock)
            {
                if (token.IsCancellationRequested || !string.Equals(_psyncHealHash, hash, StringComparison.Ordinal) || _psyncHealTries != tries) return;
                // 216: counted in PSyncOnApplyStart once the reapply really starts (a no-op request costs no try).
                _psyncHealRequestedHash = hash;
            }

            Logger.LogInformation("[{applicationId}] PSync auto-fix: {pair} has missing parts ({detail}); reapplying last data with a redraw (try {try}/{max})",
                applicationId, Pair.PairUIDName, result.Detail, tries + 1, AutoHealMaxTries);
            // Same as Reapply Last Data, without the emote sound restart: skip the duplicate-apply debounce once and redraw.
            Interlocked.Exchange(ref _forkBypassDebounceOnce, 1);
            Interlocked.Exchange(ref _redrawOnNextApplication, 1);
            Pair.ReapplyCachedData(forced: true);
        }
        catch (OperationCanceledException)
        {
            // cancelled by a newer apply, zone change or disposal
        }
        catch (ObjectDisposedException)
        {
            // disposed while waiting
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[{applicationId}] PSync auto-fix check failed for {pair}", applicationId, Pair.PairUIDName);
        }
    }
}
