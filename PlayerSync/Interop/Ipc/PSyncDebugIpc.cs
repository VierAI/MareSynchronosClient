using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using MareSynchronos.Services.Events;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.WebAPI.Files;
using MareSynchronos.WebAPI.Files.Models;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace MareSynchronos.Interop.Ipc;

/// <summary>
/// Fork (1.15.5.1210): read-only debug IPC for local debugging tools. Off unless the user enables 'Allow local debug tools to read pair info' in Settings > Debug; while off, GetPairStates returns an empty array.
/// - <c>PSync.GetPairStatesVersion</c> () -> int, currently 3.
/// - <c>PSync.GetPairStates</c> () -> string: a JSON array, one object per visible pair:
///   {objIndex, name, state (loaded/paused/redrawing/waiting), lastApplyUtc (ISO 8601 or null), dataHash, noise (active/fading[:reason]/failed/none; v3 reason: loopEnded, oneShot, timeEnded, soundRemoved, moved),
///   plugins {glamourer, penumbra, customize, heels, honorific, moodles, loci, petNames: bool, what the last apply set},
///   status (latest application status, or null), statusMessage (latest status message, or null)} (v2 adds the last three, same as Help > Pairs).
///   v3 adds: tier (0-4) and tierName, queueWaiting (files waiting for a download slot), queuePosition (best position
///   in the download queue, 1 = next, 0 = none waiting), downloadsRunning (files holding a slot), downloadStatus
///   (per-file status counts), bytesRemaining (total minus transferred over the running download batches),
///   paused (bool) and pauseReason (stored pause reason text, null if not paused).
///   The character name is for local debugging; no UIDs are returned. Nothing is written and no action is taken.
/// JSON text is returned (not a typed object) so callers in another plugin load context need no shared types.
/// </summary>
public sealed class PSyncDebugIpc : IHostedService, IMediatorSubscriber
{
    public const int Version = 3;

    private readonly ILogger<PSyncDebugIpc> _logger;
    private readonly IDalamudPluginInterface _pi;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly EventAggregator _events;
    private ICallGateProvider<string>? _states;
    private ICallGateProvider<int>? _version;
    private readonly ServerConfigurationManager _serverConfig;
    private readonly ConcurrentDictionary<DownloadBatchInfo, ConcurrentDictionary<string, FileDownloadStatus>> _downloads = new();

    public MareMediator Mediator { get; }

    public PSyncDebugIpc(ILogger<PSyncDebugIpc> logger, IDalamudPluginInterface pi, PairManager pairManager, DalamudUtilService dalamudUtil, EventAggregator events,
        MareMediator mediator, ServerConfigurationManager serverConfig)
    {
        Mediator = mediator;
        _serverConfig = serverConfig;
        Mediator.Subscribe<DownloadStartedMessage>(this, m => _downloads[m.BatchInfo] = m.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, m => _downloads.TryRemove(m.BatchInfo, out _));
        _logger = logger;
        _pi = pi;
        _pairManager = pairManager;
        _dalamudUtil = dalamudUtil;
        _events = events;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _version = _pi.GetIpcProvider<int>("PSync.GetPairStatesVersion");
            _version.RegisterFunc(() => Version);
            _states = _pi.GetIpcProvider<string>("PSync.GetPairStates");
            _states.RegisterFunc(GetPairStates);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not register the debug IPC");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _version?.UnregisterFunc();
        _states?.UnregisterFunc();
        Mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }

    private string GetPairStates()
    {
        if (!MareSynchronos.Services.VierAIConfigStore.Current.AllowLocalDebugPairInfo) return "[]";
        try
        {
            var rows = _dalamudUtil.IsOnFrameworkThread
                ? Collect()
                : _dalamudUtil.RunOnFrameworkThread(Collect).GetAwaiter().GetResult();
            return JsonSerializer.Serialize(rows);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PSync.GetPairStates failed");
            return "[]";
        }
    }

    private List<Dictionary<string, object?>> Collect()
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<Dictionary<string, object?>>();
        foreach (var pair in _pairManager.GetVisiblePairs())
        {
            int? objIndex = null;
            if (pair.ContendId != 0 && _dalamudUtil.TryGetPlayerAddressAndIndexOnFrameworkByContentId(pair.ContendId, out _, out var idx)) objIndex = idx;
            var lastApply = pair.LastDataApplicationTime;
            string state = pair.IsPaused ? "paused"
                : pair.HasDataApplicationTaskRunning ? "redrawing"
                : pair.HasCachedData && lastApply > DateTimeOffset.MinValue ? "loaded"
                : "waiting";
            string? hash = null;
            try { hash = pair.GetCopyOfPairCharacterData()?.DataHash.Value; } catch { hash = null; }
            Dictionary<string, bool>? plugins = null;
            try
            {
                var ch = pair.GetPairPlayerChanges();
                bool Has(PlayerChanges c) => ch.TryGetValue(c, out var v) && v;
                plugins = new()
                {
                    ["glamourer"] = Has(PlayerChanges.Glamourer),
                    ["penumbra"] = Has(PlayerChanges.ModFiles),
                    ["customize"] = Has(PlayerChanges.Customize),
                    ["heels"] = Has(PlayerChanges.Heels),
                    ["honorific"] = Has(PlayerChanges.Honorific),
                    ["moodles"] = Has(PlayerChanges.Moodles),
                    ["loci"] = Has(PlayerChanges.Loci),
                    ["petNames"] = Has(PlayerChanges.PetNames),
                };
            }
            catch { plugins = null; }
            string? status = null, statusMessage = null;
            try
            {
                var ev = _events.GetLatestDataEventByUid(pair.UserData.UID);
                status = ev?.ApplicationStatus.ToString();
                statusMessage = ev?.Message?.ToString();
            }
            catch { status = null; }
            list.Add(new()
            {
                ["objIndex"] = objIndex,
                ["name"] = pair.PlayerName,
                ["state"] = state,
                ["lastApplyUtc"] = lastApply > DateTimeOffset.MinValue ? lastApply.UtcDateTime.ToString("o") : null,
                ["dataHash"] = hash,
                ["noise"] = Noise(pair, now),
                ["plugins"] = plugins,
                ["status"] = status,
                ["statusMessage"] = statusMessage,
            });
            AddV3(list[^1], pair);
        }
        return list;
    }

    private static string Noise(Pair pair, DateTimeOffset now)
    {
        if (pair.SoundState == SoundState.Active) return "active";
        if (SoundActivity.IsListed(pair, now)) return pair.SoundEndReason is { } why ? "fading:" + why : "fading";
        if (pair.SoundLastFailedAt != null && (now - pair.SoundLastFailedAt.Value).TotalSeconds <= SoundActivity.FadeSeconds) return "failed";
        return "none";
    }

    private void AddV3(Dictionary<string, object?> row, Pair pair)
    {
        var q = DownloadPriorityQueue.Instance;
        try
        {
            var tier = q?.GetTier(pair);
            row["tier"] = tier;
            row["tierName"] = tier is int t ? PriorityTiers.Name(t) : null;
            if (q != null)
            {
                var (waiting, position, running) = q.GetQueueInfo(pair);
                row["queueWaiting"] = waiting;
                row["queuePosition"] = position;
                row["downloadsRunning"] = running;
            }
        }
        catch { row["tier"] ??= null; }
        try
        {
            long total = 0, done = 0;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in _downloads)
            {
                if (kv.Key.Pair != pair) continue;
                foreach (var s in kv.Value.Values)
                {
                    total += s.TotalBytes;
                    done += s.TransferredBytes;
                    var k = s.DownloadStatus.ToString();
                    counts[k] = counts.TryGetValue(k, out var c) ? c + 1 : 1;
                }
            }
            row["bytesRemaining"] = Math.Max(0, total - done);
            row["downloadStatus"] = counts;
        }
        catch { row["bytesRemaining"] = null; }
        row["paused"] = pair.IsPaused;
        try
        {
            row["pauseReason"] = pair.IsPaused ? MareSynchronos.MareConfiguration.Models.PauseReasonText.FromPauseReason(_serverConfig.GetPauseReasonForUid(pair.UserData.UID)) : null;
        }
        catch { row["pauseReason"] = null; }
    }
}
