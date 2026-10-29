using MareSynchronos.API.Data;
using MareSynchronos.MareConfiguration;
using MareSynchronos.Services.Mediator;
using MareSynchronos.WebAPI.Files.Models;
using MareSynchronos.WebAPI.SignalR;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;

namespace MareSynchronos.WebAPI.Files;

public class FileTransferOrchestrator : DisposableMediatorSubscriberBase
{
    private readonly HttpClientProvider _httpClientProvider;
    private readonly MareConfigService _mareConfig;
    private readonly TokenProvider _tokenProvider;
    private readonly object _runningDownloadsLock = new();
    private readonly ConcurrentDictionary<string, DownloadTask> _runningDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly DownloadPriorityQueue _priorityQueue;

    private readonly ConcurrentDictionary<string, byte> _hashesReportedError404 = new(StringComparer.OrdinalIgnoreCase);

    // Download slots. One semaphore for the life of the orchestrator. Swapping it
    // leaves a waiter on the old instance and Release on the new one.
    private const int MaxTransferSlots = 100;
    private readonly object _semaphoreModificationLock = new();
    private int _availableDownloadSlots;
    private readonly SemaphoreSlim _downloadSemaphore;
    private int _downloadSlotsToReclaim;
    private int CurrentlyUsedDownloadSlots => _availableDownloadSlots - _downloadSemaphore.CurrentCount + _downloadSlotsToReclaim;

    // Upload slots
    private readonly object _uploadModificationLock = new();
    private int _availableUploadSlots;
    private readonly SemaphoreSlim _uploadSemaphore;
    private int _uploadSlotsToReclaim;
    private int CurrentlyUsedUploadSlots => _availableUploadSlots - _uploadSemaphore.CurrentCount + _uploadSlotsToReclaim;

    public FileTransferOrchestrator(ILogger<FileTransferOrchestrator> logger, MareConfigService mareConfig,
        MareMediator mediator, TokenProvider tokenProvider, HttpClientProvider httpClientProvider, DownloadPriorityQueue priorityQueue) : base(logger, mediator)
    {
        _priorityQueue = priorityQueue;
        _priorityQueue.SetSlotAcquirer(() => _downloadSemaphore.Wait(0));
        _mareConfig = mareConfig;
        _tokenProvider = tokenProvider;
        _httpClientProvider = httpClientProvider;

        _availableDownloadSlots = Math.Clamp(mareConfig.Current.ParallelDownloads, 1, MaxTransferSlots);
        _downloadSemaphore = new(_availableDownloadSlots, MaxTransferSlots);

        _availableUploadSlots = Math.Clamp(mareConfig.Current.ParallelUploads, 1, MaxTransferSlots);
        // temp for now until a better config validation framework can be worked out
        if (_availableDownloadSlots != mareConfig.Current.ParallelDownloads || _availableUploadSlots != mareConfig.Current.ParallelUploads)
        {
            mareConfig.Current.ParallelDownloads = _availableDownloadSlots;
            mareConfig.Current.ParallelUploads = _availableUploadSlots;
            mareConfig.Save();
        }

        _uploadSemaphore = new(_availableUploadSlots, MaxTransferSlots);

        Mediator.Subscribe<ConnectedMessage>(this, (msg) =>
        {
            FilesCdnUri = msg.Connection.ServerInfo.FileServerAddress;
        });

        // Priority settings changed or a priority download started/finished: re-evaluate who gets the next slot.
        Mediator.Subscribe<DownloadLimitChangedMessage>(this, (msg) => _priorityQueue.Pump());
        // Priority list / tier switches / in-game friends changed: re-tier waiting and running downloads (e.g. someone added to the list while visible).
        Mediator.Subscribe<PriorityChangedMessage>(this, (msg) => _priorityQueue.Retier("a priority settings or friends change"));

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            FilesCdnUri = null;
        });
    }

    public Uri? FilesCdnUri { private set; get; }
    public List<FileTransfer> ForbiddenTransfers { get; } = [];
    public bool IsInitialized => FilesCdnUri != null;
    public HttpRequestHeaders DefaultRequestHeaders => _httpClientProvider.GetHttpClient().DefaultRequestHeaders;
    public int TimeZoneUtcOffsetMinutes
    {
        get
        {
            int result = LongitudinalRegion.FromLocalSystemTimeZone().UtcOffsetMinutes;

            if (_mareConfig.Current.OverrideCdnTimeZone)
            {
                var overrideTimeZoneId = _mareConfig.Current.OverrideCdnTimeZoneId;
                if (!string.IsNullOrEmpty(overrideTimeZoneId) && LongitudinalRegion.FromTimeZoneId(overrideTimeZoneId) is var region && region.HasValue)
                {
                    result = region.Value.UtcOffsetMinutes;
                }
                else
                {
                    result = 0;
                }
            }

            return result;
        }
    }

    /// <param name="key">The key (file hash) that was passed to <see cref="WaitForDownloadSlotAsync"/>.</param>
    public void ReleaseDownloadSlot(string? key = null)
    {
        _priorityQueue.OnSlotReleased(key);
        var released = ReleaseTransferSlot(_semaphoreModificationLock, _downloadSemaphore, ref _availableDownloadSlots, ref _downloadSlotsToReclaim, _mareConfig.Current.ParallelDownloads);
        _priorityQueue.Pump();
        if (released)
            Mediator.Publish(new DownloadLimitChangedMessage());
    }

    public void ReleaseUploadSlot()
    {
        ReleaseTransferSlot(_uploadModificationLock, _uploadSemaphore, ref _availableUploadSlots, ref _uploadSlotsToReclaim, _mareConfig.Current.ParallelUploads);
    }

    private static void ApplyTransferLimit(SemaphoreSlim semaphore, ref int currentLimit, ref int slotsToReclaim, int configured)
    {
        var desired = Math.Clamp(configured, 1, MaxTransferSlots);
        var delta = desired - currentLimit;
        if (delta == 0) return;

        if (delta > 0)
        {
            var restore = Math.Min(delta, slotsToReclaim);
            slotsToReclaim -= restore;
            var releaseCount = delta - restore;
            if (releaseCount > 0)
                semaphore.Release(releaseCount);
        }
        else
        {
            var toRemove = -delta;
            while (toRemove > 0 && semaphore.Wait(0))
                toRemove--;
            slotsToReclaim += toRemove;
        }

        currentLimit = desired;
    }

    private static bool ReleaseTransferSlot(object gate, SemaphoreSlim semaphore, ref int currentLimit, ref int slotsToReclaim, int configured)
    {
        lock (gate)
        {
            ApplyTransferLimit(semaphore, ref currentLimit, ref slotsToReclaim, configured);
            if (slotsToReclaim > 0)
            {
                slotsToReclaim--;
                return true;
            }

            try
            {
                semaphore.Release();
                return true;
            }
            catch (SemaphoreFullException)
            {
                return false;
            }
        }
    }

    public bool TryAddHashReportedError404(string fileHash)
    {
        return _hashesReportedError404.TryAdd(fileHash, 0);
    }

    private sealed class DownloadTask()
    {
        public Task Task { get; set; } = null!;
        // 1225: shared transfer token and waiter count, so a batch that is cancelled and asked again right away
        // joins the transfer that is still running instead of starting the same file again.
        public CancellationTokenSource? Cts { get; init; }
        public int Waiters;
        public int Generation;
        public bool Finished;
        public Progress<long> Progress { get; set; } = null!;
        public DownloadStatus CurrentStatus { get; set; } = DownloadStatus.Initializing;
        public event Action<DownloadStatus>? StatusUpdated;

        public void RaiseStatusUpdated(DownloadStatus status)
        {
            StatusUpdated?.Invoke(status);
            CurrentStatus = status;
        }
    }

    /// <summary>
    /// Starts the given download function if none is currently running for the given hash, or just
    /// attaches the given callbacks to the existing download if one is already in progress for the hash.
    /// </summary>
    /// <param name="hash">The mod file's hash.</param>
    /// <param name="progressCallback">A callback that is invoked when bytes of the file are downloaded.</param>
    /// <param name="statusCallback">A callback that is invoked when the status of the file download changes.</param>
    /// <param name="downloadFunc">The function to actually perform the download.</param>
    /// <returns>A Task that can be used to wait for the download to complete.</returns>
    public Task GetOrStartDownloadTask(string hash, EventHandler<long> progressCallback, Action<DownloadStatus> statusCallback, Func<string, IProgress<long>, Action<DownloadStatus>, Task> downloadFunc, int tier = PriorityTiers.Everyone, MareSynchronos.PlayerData.Pairs.Pair? pair = null)
    {
        DownloadTask result;
        bool joined;

        lock (_runningDownloadsLock)
        {
            if (_runningDownloads.TryGetValue(hash, out var active))
            {
                result = active;
                if (tier < PriorityTiers.Everyone || pair != null) _priorityQueue.Promote(hash, tier, pair); // fork: priority tiers
                result.Progress.ProgressChanged += progressCallback;
                result.StatusUpdated += statusCallback;
                joined = true;
            }
            else
            {
                var progress = new Progress<long>(value => progressCallback.Invoke(null, value));
                result = new DownloadTask { Progress = progress };
                result.StatusUpdated += statusCallback;
                _runningDownloads.TryAdd(hash, result);
                result.Task = Task.Run(async () =>
                {
                    try
                    {
                        await downloadFunc.Invoke(hash, progress, status => result.RaiseStatusUpdated(status)).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_runningDownloadsLock)
                        {
                            if (_runningDownloads.TryGetValue(hash, out var current) && ReferenceEquals(current, result))
                            {
                                _runningDownloads.TryRemove(hash, out _);
                            }
                        }
                    }
                });
                joined = false;
            }
        }

        if (joined)
        {
            Logger.LogDebug("Additional character waiting on {hash}", hash);
            statusCallback.Invoke(result.CurrentStatus);
        }

        return result.Task;
    }

    /// <summary>1225: how long a transfer nobody waits for any more keeps running, in case the same file is asked for again.</summary>
    public const int AbandonedTransferGraceMs = 4000;

    /// <summary>The configured parallel download count (Settings, Transfers).</summary>
    public int ConfiguredParallelDownloads => Math.Clamp(_mareConfig.Current.ParallelDownloads, 1, MaxTransferSlots);

    /// <summary>
    /// 1225: like <see cref="GetOrStartDownloadTask"/>, but the transfer runs on its own token. A caller whose token is
    /// cancelled stops waiting right away; the transfer itself is only cancelled when nobody has joined it again
    /// within <see cref="AbandonedTransferGraceMs"/>. A batch that is cancelled and re-requested right after (new
    /// data for the same pair, a visibility flip) therefore joins the running transfer instead of fetching it twice.
    /// </summary>
    public Task JoinOrStartDownloadAsync(string hash, EventHandler<long> progressCallback, Action<DownloadStatus> statusCallback,
        Func<string, IProgress<long>, Action<DownloadStatus>, CancellationToken, Task> downloadFunc, CancellationToken callerToken,
        int tier = PriorityTiers.Everyone, MareSynchronos.PlayerData.Pairs.Pair? pair = null)
    {
        DownloadTask result;
        bool joined;

        lock (_runningDownloadsLock)
        {
            if (_runningDownloads.TryGetValue(hash, out var active) && !(active.Cts?.IsCancellationRequested ?? false))
            {
                result = active;
                if (tier < PriorityTiers.Everyone || pair != null) _priorityQueue.Promote(hash, tier, pair);
                result.Progress.ProgressChanged += progressCallback;
                result.StatusUpdated += statusCallback;
                result.Waiters++;
                result.Generation++;
                joined = true;
            }
            else
            {
                var progress = new Progress<long>(value => progressCallback.Invoke(null, value));
                var cts = new CancellationTokenSource();
                result = new DownloadTask { Progress = progress, Cts = cts, Waiters = 1 };
                result.StatusUpdated += statusCallback;
                _runningDownloads[hash] = result;
                var started = result;
                result.Task = Task.Run(async () =>
                {
                    try
                    {
                        await downloadFunc.Invoke(hash, progress, status => started.RaiseStatusUpdated(status), cts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_runningDownloadsLock)
                        {
                            started.Finished = true;
                            if (_runningDownloads.TryGetValue(hash, out var current) && ReferenceEquals(current, started))
                            {
                                _runningDownloads.TryRemove(hash, out _);
                            }
                        }
                        cts.Dispose();
                    }
                });
                joined = false;
            }
        }

        if (joined)
        {
            Logger.LogDebug("Additional character waiting on {hash}", hash);
            statusCallback.Invoke(result.CurrentStatus);
        }

        return WaitForSharedDownloadAsync(result, hash, progressCallback, statusCallback, callerToken);
    }

    private async Task WaitForSharedDownloadAsync(DownloadTask task, string hash, EventHandler<long> progressCallback, Action<DownloadStatus> statusCallback, CancellationToken callerToken)
    {
        try
        {
            await task.Task.WaitAsync(callerToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested && !task.Task.IsCompleted)
        {
            int generation;
            bool abandoned;
            lock (_runningDownloadsLock)
            {
                task.Progress.ProgressChanged -= progressCallback;
                task.StatusUpdated -= statusCallback;
                task.Waiters = Math.Max(0, task.Waiters - 1);
                generation = task.Generation;
                abandoned = task.Waiters == 0 && !task.Finished;
            }

            if (abandoned)
            {
                _ = CancelIfStillAbandonedAsync(task, hash, generation);
            }

            throw;
        }
    }

    private async Task CancelIfStillAbandonedAsync(DownloadTask task, string hash, int generation)
    {
        try
        {
            await Task.Delay(AbandonedTransferGraceMs).ConfigureAwait(false);
            lock (_runningDownloadsLock)
            {
                if (task.Finished || task.Waiters > 0 || task.Generation != generation) return;
                // Take it out of the map now, so a request after this point starts a fresh transfer.
                if (_runningDownloads.TryGetValue(hash, out var current) && ReferenceEquals(current, task))
                {
                    _runningDownloads.TryRemove(hash, out _);
                }
            }

            Logger.LogDebug("{hash}: no batch waits for this transfer any more, cancelling it", hash);
            task.Cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // finished in the meantime
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{hash}: abandoned transfer cancel failed", hash);
        }
    }

    public async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, Uri uri,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead,
        bool withToken = true)
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        return await SendRequestInternalAsync(requestMessage, ct, httpCompletionOption, withToken).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestAsync<T>(HttpMethod method, Uri uri, T content, CancellationToken ct,
        bool withToken = true) where T : class
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        if (content is not ByteArrayContent)
            requestMessage.Content = JsonContent.Create(content);
        else
            requestMessage.Content = content as ByteArrayContent;
        return await SendRequestInternalAsync(requestMessage, ct, withToken: withToken).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestStreamAsync(HttpMethod method, Uri uri, ProgressableStreamContent content,
        CancellationToken ct, bool withToken = true)
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        requestMessage.Content = content;
        return await SendRequestInternalAsync(requestMessage, ct, withToken: withToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a download slot through the priority queue. Priority downloads are handed free slots first.
    /// </summary>
    /// <param name="key">Identifies the download (the file hash). Pass the same key to <see cref="ReleaseDownloadSlot"/>.</param>
    public async Task WaitForDownloadSlotAsync(string key, int tier, CancellationToken token, MareSynchronos.PlayerData.Pairs.Pair? pair = null)
    {
        lock (_semaphoreModificationLock)
        {
            ApplyTransferLimit(_downloadSemaphore, ref _availableDownloadSlots, ref _downloadSlotsToReclaim, _mareConfig.Current.ParallelDownloads);
        }

        await _priorityQueue.WaitForSlotAsync(key, tier, token, pair).ConfigureAwait(false);
        Mediator.Publish(new DownloadLimitChangedMessage());
    }

    public int GetTier(DownloadBatchInfo batch) => _priorityQueue.GetTier(batch);

    /// <summary>
    /// The speed limit in bytes per second for the download with this key (same for every tier).
    /// </summary>
    public long DownloadLimitFor(string? key)
    {
        return DownloadLimitPerSlot();
    }

    public long DownloadLimitPerSlot()
    {
        var limit = _mareConfig.Current.DownloadSpeedLimitInBytes;
        if (limit <= 0) return 0;
        limit = _mareConfig.Current.DownloadSpeedType switch
        {
            MareConfiguration.Models.DownloadSpeeds.Bps => limit,
            MareConfiguration.Models.DownloadSpeeds.KBps => limit * 1024,
            MareConfiguration.Models.DownloadSpeeds.MBps => limit * 1024 * 1024,
            _ => limit,
        };
        var currentUsedDlSlots = CurrentlyUsedDownloadSlots;
        var avaialble = _availableDownloadSlots;
        var currentCount = _downloadSemaphore.CurrentCount;
        var dividedLimit = limit / (currentUsedDlSlots == 0 ? 1 : currentUsedDlSlots);
        if (dividedLimit < 0)
        {
            Logger.LogWarning("Calculated Bandwidth Limit is negative, returning Infinity: {value}, CurrentlyUsedDownloadSlots is {currentSlots}, " +
                "DownloadSpeedLimit is {limit}, available slots: {avail}, current count: {count}", dividedLimit, currentUsedDlSlots, limit, avaialble, currentCount);
            return long.MaxValue;
        }
        return Math.Clamp(dividedLimit, 1, long.MaxValue);
    }

    public async Task WaitForUploadSlotAsync(CancellationToken token)
    {
        lock (_uploadModificationLock)
        {
            ApplyTransferLimit(_uploadSemaphore, ref _availableUploadSlots, ref _uploadSlotsToReclaim, _mareConfig.Current.ParallelUploads);
        }

        await _uploadSemaphore.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendRequestInternalAsync(HttpRequestMessage requestMessage,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead, bool withToken = true)
    {
        if (withToken)
        {
            var token = await _tokenProvider.GetToken().ConfigureAwait(false);
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            requestMessage.Headers.Authorization = null;
        }

        if (requestMessage.Content != null && requestMessage.Content is not StreamContent && requestMessage.Content is not ByteArrayContent)
        {
            var content = await ((JsonContent)requestMessage.Content).ReadAsStringAsync().ConfigureAwait(false);
            Logger.LogDebug("Sending {method} to {uri} (Content: {content})", requestMessage.Method, requestMessage.RequestUri, content);
        }
        else
        {
            Logger.LogDebug("Sending {method} to {uri}", requestMessage.Method, requestMessage.RequestUri);
        }

        try
        {
            if (ct != null)
                return await _httpClientProvider.GetHttpClient().SendAsync(requestMessage, httpCompletionOption, ct.Value).ConfigureAwait(false);
            return await _httpClientProvider.GetHttpClient().SendAsync(requestMessage, httpCompletionOption).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during SendRequestInternal for {uri}", requestMessage.RequestUri);
            throw;
        }
    }
}
