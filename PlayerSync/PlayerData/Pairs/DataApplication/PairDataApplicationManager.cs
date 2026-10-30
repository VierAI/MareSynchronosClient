using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Dto.User;
using MareSynchronos.Interop.Ipc;
using MareSynchronos.MareConfiguration;
using MareSynchronos.Services;
using MareSynchronos.Services.Events;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace MareSynchronos.PlayerData.Pairs;

public sealed class PairDataApplicationManager : DisposableMediatorSubscriberBase
{
    private sealed record CharacterDataApplication(OnlineUserCharaDataDto Dto, long DataApplicationSequence);

    private readonly MareConfigService _configurationService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IpcManager _ipcManager;

    private readonly object _applyDataLock = new();
    private readonly List<Pair> _applyDataQueue = [];
    private readonly Dictionary<Pair, CharacterDataApplication> _applyQueuedDataQueue = [];
    private readonly object _deferredApplicationLock = new();
    private readonly ConcurrentDictionary<Pair, CharacterDataApplication> _deferredPairDataApplications = [];
    private readonly HashSet<Pair> _runningApplyDataTasks = [];
    // Fork: per-pair retry backoff after a failed application (1 s doubling to 60 s), so a pair that keeps failing the same
    // way is not retried on every 250 ms deferred tick. Reset on success, new full data, a reapply request, or the pair leaving view.
    private readonly ConcurrentDictionary<Pair, (int Failures, DateTimeOffset NextTry, string Reason)> _failureBackoff = new();
    private static readonly TimeSpan BackoffStart = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(60);

    private CancellationTokenSource? _deferredApplicationCts;
    private CancellationTokenSource? _dataApplicationManagerCts;
    private CancellationTokenSource? _dataApplicationPipelineTopLevelCts;
    private Task? _dataApplicationManagerTask;
    private Task? _pendingDeferredTask;
    private int _maxConcurrentApplyData;

    private long _dataApplicationSequence = 0;
    private bool _isZoning = false;
    private bool _isConnected = false;
    private bool _isPlayerIdle = false;

    public PairDataApplicationManager(ILogger<PairDataApplicationManager> logger, DalamudUtilService dalamudUtilService, IpcManager ipcManager,
                MareConfigService configurationService, ServerConfigurationManager serverConfigurationManager, MareMediator mediator, PairManager pairManager)
        : base(logger, mediator)
    {
        _configurationService = configurationService;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
        _ipcManager = ipcManager;
        _pairManager = pairManager;
        _maxConcurrentApplyData = _configurationService.Current.MaxConcurrentApplications < 1 ? 1 : _configurationService.Current.MaxConcurrentApplications;

        Mediator.Subscribe<ReapplyCachedPairDataMessage>(this, (msg) => { ResetBackoff(msg.Pair); _ = ReapplyCachedPairData(msg.Pair, msg.Force); });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) =>
        {
            _isConnected = false;
            _dataApplicationPipelineTopLevelCts = _dataApplicationPipelineTopLevelCts.CancelRecreate();
            ClearDataApplicationQueues();
        });
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (_) =>
        {
            _isZoning = true;
            _dataApplicationPipelineTopLevelCts = _dataApplicationPipelineTopLevelCts.CancelRecreate();
            ClearDataApplicationQueues();
        });
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) =>
        {
            _isZoning = true;
            _dataApplicationPipelineTopLevelCts = _dataApplicationPipelineTopLevelCts.CancelRecreate();
            ClearDataApplicationQueues();
        });
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) =>
        {
            _isZoning = false;
            _isPlayerIdle = false;
        });
        Mediator.Subscribe<PairOfflineMessage>(this, (msg) =>
        {
            _deferredPairDataApplications.TryRemove(msg.Pair, out _);
        });
        Mediator.Subscribe<ConnectedMessage>(this, (_) => _isConnected = true);
        Mediator.Subscribe<ZoneSwitchEndMessage>(this, (_) => _isZoning = false);
        Mediator.Subscribe<PlayerIdleStartMessage>(this, _ => _isPlayerIdle = true);
        Mediator.Subscribe<PlayerIdleEndMessage>(this, _ => _isPlayerIdle = false);

        _deferredApplicationCts = _deferredApplicationCts.CancelRecreate();
        _pendingDeferredTask = CheckForDeferredPairDataApplication(_deferredApplicationCts.Token);

        _dataApplicationManagerCts = _dataApplicationManagerCts.CancelRecreate();
        _dataApplicationManagerTask = ProcessApplyDataQueue(_dataApplicationManagerCts.Token);

        _dataApplicationPipelineTopLevelCts = _dataApplicationPipelineTopLevelCts.CancelRecreate();

        Logger.LogTrace("{class} created.", nameof(PairDataApplicationManager));
    }

    public int MaxConcurrentApplyData
    {
        get
        {
            lock (_applyDataLock)
            {
                return _maxConcurrentApplyData;
            }
        }
        set
        {
            lock (_applyDataLock)
            {
                var max = value;
                if (max < 1)
                {
                    max = 1;
                }
                _maxConcurrentApplyData = max;
                _configurationService.Current.MaxConcurrentApplications = max;
                _configurationService.Save();
            }
        }
    }
    private bool IsDeferredDueToCombatOrPerforming => (_configurationService.Current.AutoPauseDataApplicationWhenPerforming
        && _dalamudUtilService.IsPerforming) || _dalamudUtilService.IsInCombat;
    private bool DeferringDataApplications =>
        IsDeferredDueToCombatOrPerforming
        || _isPlayerIdle
        || _dalamudUtilService.IsInCutscene
        || _dalamudUtilService.IsOccupiedInCutSceneEvent
        || _dalamudUtilService.IsInGpose
        || !_ipcManager.Penumbra.APIAvailable
        || !_ipcManager.Glamourer.APIAvailable;

    public void ProcessIncommingCharacterDataDto(OnlineUserCharaDataDto dto)
    {
        if (!_pairManager.TryGetPairByUserData(dto.User, out var pair) || pair == null)
        {
            // Fork: data for a pair we no longer have (unpaired or left the shell mid-flight, or a reconnect race). Drop it.
            Logger.LogDebug("Dropping character data for unknown pair {uid}", dto.User.UID);
            return;
        }

        CharacterDataApplication queuedData;
        queuedData = new(dto, Interlocked.Increment(ref _dataApplicationSequence));
        if (dto.AddonPlugin == null) ResetBackoff(pair); // new full data: retry right away

        if (!pair.CanApplyModdedData || DeferringDataApplications) // defer this pair for now
        {
            Logger.LogDebug("Pair is not in a valid state or we are deferring data application: {pair} CachedPlayer: {cached} " +
                "IsDeferred: {deferred} IsVisible: {visible} ModelReady: {ready} HasTaskRunning: {task}",
                pair.PairUIDName, pair.HasCachedPlayer, IsDeferredDueToCombatOrPerforming, pair.IsVisible, pair.IsModelReady, pair.HasDataApplicationTaskRunning);

            QueueOrRequeueDeferredData(pair, queuedData);
        }
        else
        {
            if (dto.AddonPlugin != null) // process the dto via the addon plugin path if AddonPlugin exists
            {
                Logger.LogTrace("Received partial character data for {pair} for plugin: {plugin}", pair.PairUIDName, dto.AddonPlugin);

                lock (_applyDataLock)
                {
                    // check if we have a pending Dto and update it so that the full update doesn't overwrite the latest state
                    if (_applyQueuedDataQueue.TryGetValue(pair, out var existingQueuedData))
                    {
                        UpdateExistingDtoAddonData(existingQueuedData.Dto, dto);
                    }
                }

                Mediator.Publish(new EventMessage(new Event(pair.UserData, nameof(PairManager), EventSeverity.Informational, "Received AddonPlugin Data")));
                pair.ApplyAddonPluginUpdate(dto);
            }
            else
            {
                Mediator.Publish(new EventMessage(new Event(pair.UserData, nameof(PairManager), EventSeverity.Informational, "Received Full Character Data")));
                AddApplyDataToQueue(pair, queuedData);
            }
        }
    }

    private void ClearDataApplicationQueues()
    {
        lock (_applyDataLock)
        {
            _applyDataQueue.Clear();
            _applyQueuedDataQueue.Clear();
            _runningApplyDataTasks.Clear();
        }

        lock (_deferredApplicationLock)
        {
            _deferredPairDataApplications.Clear();
        }
    }

    private async Task ReapplyCachedPairData(Pair pair, bool force = true)
    {
        int attempts = 0;
        bool waitForDrawReady = false;
        while (attempts < 3) // chances are if this fails we're already on to a new dto from the pair
        {
            attempts++;

            if (waitForDrawReady)
            {
                // 1225: the last try failed because the character was not in a usable state (still loading, redrawing,
                // or not drawn). Retrying at once just fails again; wait until it is drawn and ready (up to 3 s), and
                // stop when it went out of view.
                waitForDrawReady = false;
                if (!await WaitForPairDrawReadyAsync(pair, TimeSpan.FromSeconds(3)).ConfigureAwait(false))
                {
                    // 1.15.5.1414: do not drop the apply (that could leave an old look on screen). Mark it pending;
                    // PSyncCrowdService.LiftHolds reapplies it once nothing holds the pair, and drops it if they left view.
                    QueuePendingReapplyAfterDrawWait(pair);
                    return;
                }
            }

            bool hadErrors = false;
            bool started = false;
            bool completed = false;

            Guid applicationBase = Guid.NewGuid();
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                if (pair == null || pair.CachedPlayer == null || _dataApplicationPipelineTopLevelCts.IsCancellationRequested || _dataApplicationManagerCts.IsCancellationRequested 
                    || !_isConnected || !pair.IsVisible || !pair.HasCachedData)
                {
                    Logger.LogDebug("Cached data application for {pair} stopped! {pair} {cached} {conn} {visible} {data}",
                        pair?.PairUIDName ?? string.Empty, pair == null, pair.CachedPlayer == null, !_isConnected, !pair.IsVisible, !pair.HasCachedData);
                    return;
                }

                if (MareSynchronos.Services.PSyncCrowd.HoldsApply(pair))
                {
                    // 1.15.5.1217: blocked / budget hold / not drawn (tier 3-4): remember and reapply when the hold lifts.
                    MareSynchronos.Services.PSyncCrowd.PendingReapply[pair.UserData.UID] = 0;
                    Logger.LogDebug("Cached data application for {pair} held: {why}", pair.PairUIDName, MareSynchronos.Services.PSyncCrowd.DescribeWait(pair));
                    return;
                }

                started = pair.TryStartFullDataApplication(applicationBase, false);
                if (!started)
                {
                    Logger.LogTrace("[BASE-{appBase}] Can't apply cached data application for {pair}, another application is already running!",
                        applicationBase, pair.PairUIDName);

                    break;
                }

                Logger.LogTrace("[BASE-{appBase}] Starting cached data application for {pair}", applicationBase, pair.PairUIDName);
                await pair.ApplyCachedDataAync(applicationBase, _dataApplicationPipelineTopLevelCts.Token, force).ConfigureAwait(false);
                completed = true;
                waitForDrawReady = false;

                break;
            }
            catch (OperationCanceledException)
            {
                hadErrors = true;
                Mediator.Publish(new EventMessage(new DataApplicationEvent(applicationBase, DataApplicationStatus.Cancelled, pair.PlayerName,
                        pair.UserData, nameof(ApplyPairDataAsync), EventSeverity.Warning, $"Cached data application was cancelled for {pair.PairUIDName}")));
            }
            catch (PairDataApplicationException ex)
            {
                hadErrors = true;
                Mediator.Publish(new EventMessage(new DataApplicationEvent(applicationBase, DataApplicationStatus.Failed, pair.PlayerName,
                        pair.UserData, nameof(ApplyPairDataAsync), EventSeverity.Error, $"Cached data application failed for {pair.PairUIDName}, {ex.Reason}")));

                if (ex.Reason != PairDataExceptionReason.InvalidCharacterState)
                {
                    Logger.LogWarning("[BASE-{appBase}] An error occured during cached data application for {pair}: {reason} - {msg}",
                        applicationBase, pair.PairUIDName, ex.Reason, ex.Message);
                }
                else
                {
                    // 1.15.5.1403: DownloadPipeline legacy (1224) retries at once, without the 1225 draw-ready wait
                    waitForDrawReady = true;
                }

                continue;
            }
            catch (Exception ex)
            {
                hadErrors = true;
                Mediator.Publish(new EventMessage(new DataApplicationEvent(applicationBase, DataApplicationStatus.Failed, pair.PlayerName,
                        pair.UserData, nameof(ApplyPairDataAsync), EventSeverity.Error, $"Cached data application failed for {pair.PairUIDName}, {ex.Message}")));

                Logger.LogError(ex, "[BASE-{appBase}] An error occured during cached data application for {pair}", applicationBase, pair.PairUIDName);
            }
            finally
            {
                stopwatch.Stop();
                if (started)
                {
                    pair.EndDataApplication(applicationBase);
                }

                if (completed && !hadErrors)
                {
                    Mediator.Publish(new EventMessage(new DataApplicationEvent(applicationBase, DataApplicationStatus.Completed, pair.PlayerName, pair.UserData,
                        nameof(ApplyPairDataAsync), EventSeverity.Informational,
                        $"Cached data application task for {pair.PairUIDName} completed in {stopwatch.ElapsedMilliseconds}ms")));
                }
            }
        }

        if (waitForDrawReady)
        {
            // 1.15.5.1414: all attempts hit an invalid character state; retry later instead of dropping it.
            QueuePendingReapplyAfterDrawWait(pair);
        }
    }

    /// <summary>1.15.5.1414: an apply that gave up because the character never became drawn and ready is retried later.</summary>
    private void QueuePendingReapplyAfterDrawWait(Pair? pair)
    {
        if (pair == null || !pair.IsVisible || !pair.HasCachedData) return;
        MareSynchronos.Services.PSyncCrowd.PendingReapply[pair.UserData.UID] = 0;
        Logger.LogDebug("Cached data application for {pair} not drawn and ready after an invalid-state retry; queued a pending reapply", pair.PairUIDName);
    }

    /// <summary>1225: true once the pair is visible and its model is drawn and ready; false on timeout, invisibility or shutdown.</summary>
    private async Task<bool> WaitForPairDrawReadyAsync(Pair? pair, TimeSpan timeout)
    {
        if (pair == null) return false;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var topCts = _dataApplicationPipelineTopLevelCts;
            if (topCts == null || topCts.IsCancellationRequested || (_dataApplicationManagerCts?.IsCancellationRequested ?? true) || !_isConnected)
                return false;
            if (!pair.IsVisible || pair.CachedPlayer == null)
                return false;
            if (pair.IsModelReady)
                return true;
            try
            {
                await Task.Delay(100, topCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        return pair.IsVisible && pair.IsModelReady;
    }

    private void QueueOrRequeueDeferredData(Pair pair, CharacterDataApplication incomingQueuedData)
    {
        lock (_deferredApplicationLock)
        {
            // handle requeue of data when data application is in progress
            // this is different than updating queued data since the data is no longer queued when in flight
            // important so that cached data during pair visibility changes doesn't overwrite newer incoming dtos
            bool isRunning;
            lock (_applyDataLock)
            {
                isRunning = _runningApplyDataTasks.Contains(pair);
            }
            if (isRunning)
            {
                // only would be true if requeued
                bool runningDataApplicationIsNewer = pair.PendingDataSequenceNumber > incomingQueuedData.DataApplicationSequence;

                // we requeued a full data application but the running one is already newer
                // note that we don't requeue partial data applications, this is more of a safety check
                if (runningDataApplicationIsNewer && incomingQueuedData.Dto.AddonPlugin == null)
                {
                    return;
                }
            }

            // handles updating queued data as new dto come in
            _deferredPairDataApplications.AddOrUpdate(pair, incomingQueuedData, (_, existingQueuedData) =>
            {
                if (incomingQueuedData.DataApplicationSequence > existingQueuedData.DataApplicationSequence)
                {
                    if (incomingQueuedData.Dto.AddonPlugin == null)
                    {
                        return incomingQueuedData; // newer full update, so use this one
                    }

                    lock (_applyDataLock)
                    {
                        // Incoming is newer, but it's also just a partial, so update the existing dto
                        UpdateExistingDtoAddonData(existingQueuedData.Dto, incomingQueuedData.Dto);
                    }

                    return new CharacterDataApplication(existingQueuedData.Dto, incomingQueuedData.DataApplicationSequence);
                }
                if (existingQueuedData.Dto.AddonPlugin != null)
                {
                    lock (_applyDataLock)
                    {
                        UpdateExistingDtoAddonData(incomingQueuedData.Dto, existingQueuedData.Dto);
                    }

                    return incomingQueuedData;
                }

                return existingQueuedData;
            });
        }

        Mediator.Publish(new EventMessage(new DataApplicationEvent(Guid.Empty, DataApplicationStatus.Deferred, pair.PlayerName, pair.UserData,
            nameof(QueueOrRequeueDeferredData), EventSeverity.Informational, $"Queueing {pair.PairUIDName}.")));
    }

    private static void UpdateExistingDtoAddonData(OnlineUserCharaDataDto existingDto, OnlineUserCharaDataDto newDto)
    {
        if (newDto.AddonPlugin == AddonPlugin.Heels)
        {
            existingDto.CharaData.HeelsData = newDto.CharaData.HeelsData;
        }
        else if (newDto.AddonPlugin == AddonPlugin.Honorific)
        {
            existingDto.CharaData.HonorificData = newDto.CharaData.HonorificData;
        }
        else if (newDto.AddonPlugin == AddonPlugin.Moodles)
        {
            existingDto.CharaData.MoodlesData = newDto.CharaData.MoodlesData;
        }
        else if (newDto.AddonPlugin == AddonPlugin.Loci)
        {
            existingDto.CharaData.LociData = newDto.CharaData.LociData;
        }
        else if (newDto.AddonPlugin == AddonPlugin.PetNames)
        {
            existingDto.CharaData.PetNamesData = newDto.CharaData.PetNamesData;
        }
        else
        {
            throw new InvalidDataException("Got an unknown AddonPlugin type!");
        }
    }

    // enqueues a pair and dto, or updates the dto if the data application hasn't run yet for the pair
    private void AddApplyDataToQueue(Pair pair, CharacterDataApplication queuedData)
    {
        lock (_applyDataLock)
        {
            if (_applyQueuedDataQueue.TryGetValue(pair, out var existingQueuedData) &&
                existingQueuedData.DataApplicationSequence > queuedData.DataApplicationSequence)
            {
                return;
            }
            _applyQueuedDataQueue[pair] = queuedData; // save the pair-to-dto mapping and only allow one instance/latest dto

            // as a default action, we put direct pairs in the front of the queue
            if (_applyDataQueue.Contains(pair))
            {
                if (pair.IsDirectlyPaired)
                {
                    _applyDataQueue.Remove(pair);
                    _applyDataQueue.Insert(0, pair);
                }
            }
            else
            {
                if (pair.IsDirectlyPaired)
                {
                    _applyDataQueue.Insert(0, pair);
                }
                else
                {
                    _applyDataQueue.Add(pair);
                }
            }
        }
        Mediator.Publish(new EventMessage(new DataApplicationEvent(Guid.Empty, DataApplicationStatus.Queued, pair.PlayerName, pair.UserData,
            nameof(AddApplyDataToQueue), EventSeverity.Informational, $"Queueing data application for {pair.PairUIDName}.")));
    }

    // This runs through the queue and kicks off the data application task per queued pair entry
    private async Task ProcessApplyDataQueue(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!_isConnected || _isZoning)
                    {
                        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

                        continue;
                    }

                    List<(Pair, CharacterDataApplication)> dataToApply = [];

                    lock (_applyDataLock)
                    {
                        while (_applyDataQueue.Count > 0 && (_maxConcurrentApplyData == 0 || _runningApplyDataTasks.Count < _maxConcurrentApplyData))
                        {
                            // find the next pair in the queu that is not already running
                            var index = _applyDataQueue.FindIndex(pair => !_runningApplyDataTasks.Contains(pair));

                            if (index < 0)
                            {
                                break;
                            }

                            var pair = _applyDataQueue[index];
                            _applyDataQueue.RemoveAt(index);

                            if (!_applyQueuedDataQueue.Remove(pair, out var queuedData))
                            {
                                continue;
                            }

                            _runningApplyDataTasks.Add(pair);
                            dataToApply.Add((pair, queuedData));
                        }
                    }

                    foreach (var pairToDto in dataToApply)
                    {
                        // each task will remove its pair in the finally clause
                        _ = ApplyPairDataAsync(pairToDto.Item1, pairToDto.Item2);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //
            }
        }
    }

    // This handles running and awaiting the data application pipeline
    private async Task ApplyPairDataAsync(Pair pair, CharacterDataApplication queuedData)
    {
        Stopwatch? stopwatch = null;
        var dto = queuedData.Dto;
        var version = queuedData.DataApplicationSequence;
        bool hadErrors = false;
        bool hasStarted = false;
        bool failureLogged = false;
        bool thresholdPaused = false;
        Guid applicationBase = Guid.NewGuid();

        try
        {
            _dataApplicationPipelineTopLevelCts!.Token.ThrowIfCancellationRequested();

            if (!pair.CanApplyModdedData || DeferringDataApplications)
            {
                QueueOrRequeueDeferredData(pair, queuedData);

                return;
            }

            if (!pair.TryStartFullDataApplication(applicationBase, false)) // this will set the gate if it returns true
            {
                throw new PairDataApplicationException(PairDataExceptionReason.ApplicationTaskRunning,
                    $"[BASE-{applicationBase}] Tried to start data application but another task is already running!");
            }

            stopwatch = Stopwatch.StartNew();
            hasStarted = true;

            Logger.LogDebug("[BASE-{appBase}] Starting queued data application for {pair}", applicationBase, pair.PairUIDName);
            // The entire pipeline chains a Task so we know when it's finished (or fails)
            // If a pair is no longer valid by the time it gets through the queue, the pipeline will throw and requeue the data application
            await pair.ApplyDataAsync(applicationBase, dto, version, _dataApplicationPipelineTopLevelCts!.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            hadErrors = true;

            Mediator.Publish(new EventMessage(new DataApplicationEvent(applicationBase, DataApplicationStatus.Cancelled, pair.PlayerName,
                    pair.UserData, nameof(ApplyPairDataAsync), EventSeverity.Warning, $"Data application was cancelled for {pair.PairUIDName}")));

            Logger.LogDebug("[BASE-{appBase}] Queued data application was cancelled for {pair}", applicationBase, pair.PairUIDName);
            failureLogged = true; // a cancel (newer data, pair left view) is not a failure; no warning, no backoff
        }
        catch (PairDataApplicationException ex)
        {
            hadErrors = true;

            if (ex.Reason == PairDataExceptionReason.ThresholdViolation)
            {
                thresholdPaused = true; // paused by the height/threshold limit: expected, logged at Information below
                return;
            }

            if (!_dataApplicationPipelineTopLevelCts!.Token.IsCancellationRequested && _isConnected && !_isZoning)
            {
                Mediator.Publish(new EventMessage(new DataApplicationEvent(applicationBase, DataApplicationStatus.Failed, pair.PlayerName,
                    pair.UserData, nameof(ApplyPairDataAsync), EventSeverity.Error, $"Requeueing failed data application for {pair.PairUIDName}, {ex.Reason}")));

                Logger.LogDebug("[BASE-{appBase}] Requeueing failed data application for {pair} due to {reason}", applicationBase, pair.PairUIDName, ex.Reason);
                RecordFailure(applicationBase, pair, ex.Reason + ": " + ex.Message);
                failureLogged = true;
                QueueOrRequeueDeferredData(pair, queuedData);
            }
        }
        catch (Exception ex)
        {
            hadErrors = true;

            Mediator.Publish(new EventMessage(new DataApplicationEvent(applicationBase, DataApplicationStatus.Failed, pair.PlayerName,
                    pair.UserData, nameof(ApplyPairDataAsync), EventSeverity.Error, $"Data application failed for {pair.PairUIDName}, {ex.Message}")));

            Logger.LogError(ex, "[BASE-{appBase}] Failed to apply queued pair data for {pair}", applicationBase, pair.PairUIDName);
            RecordFailure(applicationBase, pair, ex.GetType().Name + ": " + ex.Message, logWarning: false);
            failureLogged = true;
        }
        finally
        {
            if (hasStarted)
            {
                pair.EndDataApplication(applicationBase); // release the gate
                stopwatch?.Stop();
                if (hadErrors)
                {
                    // Fork: the reason is logged once by RecordFailure; this per-attempt line is debug only (it spammed every 250 ms).
                    if (thresholdPaused)
                        Logger.LogInformation("[BASE-{appBase}] Queued data application was cancelled for {pair} after {elapsedMs}ms (paused by the threshold limit)",
                            applicationBase, pair.PairUIDName, stopwatch!.ElapsedMilliseconds);
                    else if (failureLogged)
                        Logger.LogDebug("[BASE-{appBase}] Queued data application was cancelled or failed for {pair} after {elapsedMs}ms",
                            applicationBase, pair.PairUIDName, stopwatch!.ElapsedMilliseconds);
                    else
                        Logger.LogWarning("[BASE-{appBase}] Queued data application was cancelled or failed for {pair} after {elapsedMs}ms",
                            applicationBase, pair.PairUIDName, stopwatch!.ElapsedMilliseconds);
                }
                else
                {
                    ResetBackoff(pair);
                    Mediator.Publish(new EventMessage(new DataApplicationEvent(applicationBase, DataApplicationStatus.Completed, pair.PlayerName, pair.UserData,
                        nameof(ApplyPairDataAsync), EventSeverity.Informational,
                        $"Data application task for {pair.PairUIDName} completed in {stopwatch!.ElapsedMilliseconds}ms{QueueSuffix(pair)}")));

                    Logger.LogDebug("[BASE-{appBase}] Queued data application finished for {pair} in {elapsedMs}ms",
                        applicationBase, pair.PairUIDName, stopwatch!.ElapsedMilliseconds);
                }

                await Task.Delay(20 + Random.Shared.Next(1, 10), _dataApplicationPipelineTopLevelCts!.Token).ConfigureAwait(false);
            }

            lock (_applyDataLock)
            {
                _runningApplyDataTasks.Remove(pair); // ensure we always remove the entry, pass or fail
            }
        }
    }

    private async Task CheckForDeferredPairDataApplication(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_isConnected && !_isZoning)
                {
                    foreach (Pair pair in _deferredPairDataApplications.Keys)
                    {
                        lock (_deferredApplicationLock) // this gets cleared when you zone, we need to not be acting on it while it gets cleared
                        {
                            if (pair.CanApplyModdedData && !DeferringDataApplications && !IsBackingOff(pair) && _deferredPairDataApplications.TryRemove(pair, out var queuedData))
                            {
                                Logger.LogTrace("Pending deferred pair data applications remaining: {count}", _deferredPairDataApplications.Count);
                                Logger.LogDebug("Pair is no longer deferred and has data application: {pair}", pair.PairUIDName);
                                Mediator.Publish(new EventMessage(new Event(pair.UserData, nameof(PairManager), EventSeverity.Informational, "Applying deferred pair data")));
                                if (queuedData.Dto.AddonPlugin != null)
                                {
                                    pair.ApplyAddonPluginUpdate(queuedData.Dto);
                                }
                                else
                                {
                                    AddApplyDataToQueue(pair, queuedData);
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to process deferred pair data applications");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
    }

    private void RecordFailure(Guid applicationBase, Pair pair, string reason, bool logWarning = true)
    {
        var now = DateTimeOffset.UtcNow;
        var reasonChanged = _failureBackoff.TryGetValue(pair, out var prev) && !string.Equals(prev.Reason, reason, StringComparison.Ordinal);
        var state = _failureBackoff.AddOrUpdate(pair,
            _ => (1, now + BackoffStart, reason),
            (_, old) =>
            {
                var failures = old.Failures + 1;
                var delay = TimeSpan.FromTicks(Math.Min(BackoffCap.Ticks, BackoffStart.Ticks << Math.Min(failures - 1, 16)));
                return (failures, now + delay, reason);
            });
        var delayNow = state.NextTry - now;
        // Warn on the first failure, when the reason changes, and once when the retry delay reaches the cap; debug otherwise.
        var first = state.Failures == 1;
        var reachedCap = delayNow >= BackoffCap && (BackoffStart.Ticks << Math.Min(state.Failures - 2, 16)) < BackoffCap.Ticks;
        if (logWarning && (first || reasonChanged || reachedCap))
        {
            Logger.LogWarning("[BASE-{appBase}] Data application failed for {pair}: {reason}. Retry {n} in {delay:0.#} s{cap}",
                applicationBase, pair.UserData.UID, reason, state.Failures, delayNow.TotalSeconds,
                reachedCap ? " (retry delay capped at 60 s; further failures are logged at debug level)" : string.Empty);
        }
        else
        {
            Logger.LogDebug("[BASE-{appBase}] Data application failed again for {pair} ({n}x): {reason}, next retry in {delay:0.#} s",
                applicationBase, pair.UserData.UID, state.Failures, reason, delayNow.TotalSeconds);
        }
    }

    private void ResetBackoff(Pair pair) => _failureBackoff.TryRemove(pair, out _);

    private bool IsBackingOff(Pair pair)
    {
        if (!_failureBackoff.TryGetValue(pair, out var state)) return false;
        if (!pair.IsVisible) { _failureBackoff.TryRemove(pair, out _); return false; } // left view: start fresh when they come back
        return DateTimeOffset.UtcNow < state.NextTry;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        var dataApplicationPipelineTopLevelCts = _dataApplicationPipelineTopLevelCts;
        var deferredApplicationCts = _deferredApplicationCts;
        var dataApplicationManagerCts = _dataApplicationManagerCts;
        var dataApplicationManagerTask = _dataApplicationManagerTask;
        var pendingDeferredTask = _pendingDeferredTask;

        dataApplicationPipelineTopLevelCts?.Cancel();
        deferredApplicationCts?.Cancel();
        dataApplicationManagerCts?.Cancel();

        List<Task> backgroundTasks = [];

        if (dataApplicationManagerTask != null)
        {
            backgroundTasks.Add(dataApplicationManagerTask);
        }

        if (pendingDeferredTask != null)
        {
            backgroundTasks.Add(pendingDeferredTask);
        }

        var backgroundTaskCompletion = Task.WhenAll(backgroundTasks);

        try
        {
            // to stop the dispose null ref
            backgroundTaskCompletion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (backgroundTaskCompletion.IsCanceled)
        {
            //
        }
        catch (Exception ex)
        {
            Logger.LogWarning(backgroundTaskCompletion.Exception ?? ex, "One or more data application background tasks failed during disposal");
        }

        ClearDataApplicationQueues();

        dataApplicationPipelineTopLevelCts?.Dispose();
        deferredApplicationCts?.Dispose();
        dataApplicationManagerCts?.Dispose();

        _dataApplicationPipelineTopLevelCts = null;
        _deferredApplicationCts = null;
        _dataApplicationManagerCts = null;
        _dataApplicationManagerTask = null;
        _pendingDeferredTask = null;
    }

    // Club fix: tier and longest download slot wait, so a slow apply shows whether it was the queue or the download.
    private static string QueueSuffix(Pair pair)
    {
        var q = MareSynchronos.WebAPI.Files.DownloadPriorityQueue.Instance;
        if (q == null) return string.Empty;
        try
        {
            var tier = q.GetTier(pair);
            return $" (tier {tier} {MareSynchronos.WebAPI.Files.PriorityTiers.Name(tier)}, longest queue wait {q.TakeQueueWaitMs(pair)}ms)";
        }
        catch { return string.Empty; }
    }
}
