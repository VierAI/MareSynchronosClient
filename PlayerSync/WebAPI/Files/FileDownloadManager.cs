using Dalamud.Utility;
using K4os.Compression.LZ4.Legacy;
using MareSynchronos.API.Data;
using MareSynchronos.API.Dto.Files;
using MareSynchronos.API.Routes;
using MareSynchronos.FileCache;
using MareSynchronos.MareConfiguration.Configurations;
using MareSynchronos.Services.Mediator;
using MareSynchronos.WebAPI.Files.Models;
using Microsoft.Extensions.Logging;
using PlayerSync.FileCache;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace MareSynchronos.WebAPI.Files;

public partial class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<string, FileDownloadStatus> _downloadStatus;
    private readonly FileCompactor _fileCompactor;
    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly ICompressedAlternateManager _compressedAlternateManager;

    private readonly object _downloadInfoLock = new object();
    private readonly List<ThrottledStream> _activeDownloadStreams;
    private static int _downloadHaltRefCount;
    private readonly ConcurrentDictionary<string, byte> _reportedHashes = new(StringComparer.OrdinalIgnoreCase);

    public FileDownloadManager(ILogger<FileDownloadManager> logger, MareMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor, ICompressedAlternateManager compressedAlternateManager) : base(logger, mediator)
    {
        _downloadStatus = new ConcurrentDictionary<string, FileDownloadStatus>(StringComparer.Ordinal);
        _orchestrator = orchestrator;
        _fileDbManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _compressedAlternateManager = compressedAlternateManager;
        _activeDownloadStreams = [];

        //Mediator.Subscribe<DownloadLimitChangedMessage>(this, (msg) =>
        //{
        //    lock (_downloadInfoLock)
        //    {
        //        if (!_activeDownloadStreams.Any()) return;
        //        var newLimit = _orchestrator.DownloadLimitPerSlot();
        //        Logger.LogTrace("Setting new Download Speed Limit to {newLimit}", newLimit);
        //        foreach (var stream in _activeDownloadStreams)
        //        {
        //            stream.BandwidthLimit = newLimit;
        //        }
        //    }
        //});
        Mediator.Subscribe<DownloadLimitChangedMessage>(this, (msg) =>
        {
            lock (_downloadInfoLock)
            {
                if (!_activeDownloadStreams.Any())
                {
                    return;
                }

                var changedStreams = 0;

                foreach (var stream in _activeDownloadStreams)
                {
                    var newLimit = _orchestrator.DownloadLimitFor(stream.Tag); // fork: tier-aware limit
                    if (stream.BandwidthLimit == newLimit)
                    {
                        continue;
                    }
                    stream.BandwidthLimit = newLimit;
                    changedStreams++;
                }

                if (changedStreams > 0)
                {
                    Logger.LogTrace("Updated download speed limits for {count} active streams", changedStreams);
                }
            }
        });
    }

    public List<DownloadFileTransfer> CurrentDownloads { get; private set; } = [];

    public List<FileTransfer> ForbiddenTransfers => _orchestrator.ForbiddenTransfers;

    // Fork bug fix: was inverted (!Any).
    public bool IsDownloading
    {
        get
        {
            lock (_downloadInfoLock)
            {
                return CurrentDownloads.Count > 0;
            }
        }
    }

    // Fork bug fix: one failed or cancelled file no longer clears the whole batch list.
    private void RemoveDownload(DownloadFileTransfer download)
    {
        lock (_downloadInfoLock)
        {
            CurrentDownloads.Remove(download);
        }

        if (!string.IsNullOrEmpty(download.DirectDownloadUrl))
            _downloadStatus.TryRemove(download.DirectDownloadUrl, out _);
    }

    public static void MungeBuffer(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; ++i)
        {
            buffer[i] ^= 42;
        }
    }

    public void ClearDownload()
    {
        CurrentDownloads.Clear();
        _downloadStatus.Clear();
    }

    public async Task DownloadFiles(DownloadBatchInfo batchInfo, List<FileReplacementData> fileReplacementDto, Dictionary<string, string> compressionSubstitutions, CancellationToken ct)
    {
        var shouldPublishHalt = Interlocked.Increment(ref _downloadHaltRefCount) == 1;
        if (shouldPublishHalt)
            SafePublish(() => Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles))),
                        "HaltScan(DownloadFiles)");

        try
        {
            await DownloadFilesInternal(batchInfo, fileReplacementDto, compressionSubstitutions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ClearDownload();
            Logger.LogInformation("DownloadFiles cancelled");
        }
        catch (Exception ex)
        {
            ClearDownload();
            Logger.LogError(ex, "DownloadFiles failed unexpectedly");
            // swallow to reach finally and decrement the refcount
        }
        finally
        {
            SafePublish(() => Mediator.Publish(new DownloadFinishedMessage(batchInfo)), "DownloadFinishedMessage");

            var newCount = Interlocked.Decrement(ref _downloadHaltRefCount);
            if (newCount == 0)
                SafePublish(() => Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles))), "ResumeScan(DownloadFiles)");
            else if (newCount < 0)
            {
                Interlocked.Exchange(ref _downloadHaltRefCount, 0);
                Logger.LogError("DownloadFiles halt refcount went negative; resetting to 0");
                SafePublish(() => Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles))), "ResumeScan(DownloadFiles/reset)");
            }
        }
    }

    private void SafePublish(Action publish, string what)
    {
        try { publish(); }
        catch (Exception ex) { Logger.LogWarning(ex, "{what} publish threw", what); }
    }

    protected override void Dispose(bool disposing)
    {
        ClearDownload();
        lock (_downloadInfoLock)
        {
            foreach (var stream in _activeDownloadStreams.ToList())
            {
                try
                {
                    stream.Dispose();
                }
                catch
                {
                    // do nothing
                    //
                }
            }
        }
        base.Dispose(disposing);
    }

    private delegate void DownloadDataCallback(Span<byte> data);

    // 1225: download pipeline tuning. Under Wine every file and socket call goes through the single wineserver
    // thread, so a batch of 19+ files arriving at once stalls the game. Batches are started a few files at a time,
    // small files stay in memory (no temp file write, read back and delete), and decompress + hash + write run as
    // one throttled step off the game thread. PC values keep the old throughput.
    private const int StreamBufferSize = 128 * 1024;
    private const int MaxReadSlice = 256 * 1024;
    private const long InMemoryFileLimit = 32L * 1024 * 1024;
    private const long InMemoryBudget = 192L * 1024 * 1024;
    private const int MaxHttpRetries = 2;
    private static long s_inMemoryBytes;
    private static readonly SemaphoreSlim s_finalizeGate = new(LowPriorityFile.IsWine ? 1 : 3);

    // 1.15.5.1226: Wine only. Big bursts (60-108 files from several players at once) still bunched up their file starts across
    // batches, so new transfers are capped for the whole plugin: at most WineMaxFileStartsPerSecond in any rolling second and
    // at least WineMinFileStartGapMs apart. A waiter that is cancelled takes no start. PC is not affected.
    private const int WineMaxFileStartsPerSecond = 10;
    private const int WineMinFileStartGapMs = 50;
    private static readonly object s_wineStartLock = new();
    private static readonly Queue<DateTime> s_wineStarts = new();

    // 1.15.5.1227: throttled summary of the Wine start cap so logs show it pacing. One Information line per window of at least
    // WineCapLogWindowMs with starts in it (starts, how many had to wait, longest wait); a window left open when a burst ends
    // (no start for WineCapLogIdleMs) is written at the next start.
    private const int WineCapLogWindowMs = 1000;
    private const int WineCapLogIdleMs = 2000;
    private static DateTime s_wineCapWindowStart;
    private static DateTime s_wineCapLastStart;
    private static int s_wineCapStarts;
    private static int s_wineCapWaited;
    private static double s_wineCapMaxWaitMs;

    private readonly record struct WineCapSummary(int Starts, int Waited, double MaxWaitMs, double WindowMs);

    // 1.15.5.1414: WineStartCap is off by default (legacy = no pacing of file starts). On Mac it made players wait up to
    // ~16 s before their files started loading. Kept as a switch for A/B tests only.
    private static readonly bool WineStartCapEnabled = false;

    private static async Task WineFileStartPaceAsync(ILogger logger, CancellationToken token)
    {
        if (!WineStartCapEnabled || !LowPriorityFile.IsWine) return;

        var firstTry = DateTime.UtcNow;
        var hadToWait = false;
        while (true)
        {
            TimeSpan wait;
            WineCapSummary? closedIdle = null;
            WineCapSummary? closedWindow = null;
            lock (s_wineStartLock)
            {
                var now = DateTime.UtcNow;
                while (s_wineStarts.Count > 0 && now - s_wineStarts.Peek() >= TimeSpan.FromSeconds(1)) s_wineStarts.Dequeue();
                var gapWait = TimeSpan.Zero;
                if (s_wineStarts.Count > 0)
                {
                    var last = s_wineStarts.Last();
                    var sinceLast = now - last;
                    if (sinceLast < TimeSpan.FromMilliseconds(WineMinFileStartGapMs)) gapWait = TimeSpan.FromMilliseconds(WineMinFileStartGapMs) - sinceLast;
                }
                var windowWait = s_wineStarts.Count >= WineMaxFileStartsPerSecond ? s_wineStarts.Peek().AddSeconds(1) - now : TimeSpan.Zero;
                wait = gapWait > windowWait ? gapWait : windowWait;
                if (wait <= TimeSpan.Zero)
                {
                    s_wineStarts.Enqueue(now);

                    // 1227 summary bookkeeping (counters only, logging happens outside the lock)
                    if (s_wineCapStarts > 0 && (now - s_wineCapLastStart).TotalMilliseconds >= WineCapLogIdleMs)
                    {
                        closedIdle = new WineCapSummary(s_wineCapStarts, s_wineCapWaited, s_wineCapMaxWaitMs, (s_wineCapLastStart - s_wineCapWindowStart).TotalMilliseconds);
                        s_wineCapStarts = 0;
                    }

                    if (s_wineCapStarts == 0)
                    {
                        s_wineCapWindowStart = now;
                        s_wineCapWaited = 0;
                        s_wineCapMaxWaitMs = 0;
                    }

                    s_wineCapStarts++;
                    s_wineCapLastStart = now;
                    if (hadToWait)
                    {
                        s_wineCapWaited++;
                        var waitedMs = (now - firstTry).TotalMilliseconds;
                        if (waitedMs > s_wineCapMaxWaitMs) s_wineCapMaxWaitMs = waitedMs;
                    }

                    var windowMs = (now - s_wineCapWindowStart).TotalMilliseconds;
                    if (windowMs >= WineCapLogWindowMs)
                    {
                        closedWindow = new WineCapSummary(s_wineCapStarts, s_wineCapWaited, s_wineCapMaxWaitMs, windowMs);
                        s_wineCapStarts = 0;
                    }
                }
            }

            if (closedIdle is { } idle) LogWineCapSummary(logger, idle);
            if (closedWindow is { } win) LogWineCapSummary(logger, win);
            if (wait <= TimeSpan.Zero) return;

            hadToWait = true;
            await Task.Delay(wait + TimeSpan.FromMilliseconds(1), token).ConfigureAwait(false);
        }
    }

    private static void LogWineCapSummary(ILogger logger, WineCapSummary s)
    {
        logger.LogInformation("Wine file-start cap: {starts} starts in {windowMs:0} ms, {waited} waited, max wait {maxWaitMs:0} ms (cap {cap}/s, min gap {gap} ms)",
            s.Starts, s.WindowMs, s.Waited, s.MaxWaitMs, WineMaxFileStartsPerSecond, WineMinFileStartGapMs);
    }

    private sealed class DownloadedPayload
    {
        public byte[]? Memory { get; init; }
        public long ReservedBytes { get; set; }
        public string? TempFile { get; init; }

        public void ReleaseReservation()
        {
            if (ReservedBytes > 0)
            {
                Interlocked.Add(ref s_inMemoryBytes, -ReservedBytes);
                ReservedBytes = 0;
            }
        }
    }

    private static bool IsRetryableStatus(HttpStatusCode? code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (code.HasValue && (int)code.Value >= 500 && (int)code.Value <= 599);

    private string BuildHeaderDump(HttpResponseMessage response)
    {
        var headersBuilder = new StringBuilder();
        if (response.RequestMessage == null) return string.Empty;
        headersBuilder.AppendLine("DefaultRequestHeaders:");
        foreach (var header in _orchestrator.DefaultRequestHeaders)
        {
            foreach (var value in header.Value)
            {
                headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
            }
        }
        headersBuilder.AppendLine("RequestMessage.Headers:");
        foreach (var header in response.RequestMessage.Headers)
        {
            foreach (var value in header.Value)
            {
                headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
            }
        }
        if (response.RequestMessage.Content != null)
        {
            headersBuilder.AppendLine("RequestMessage.Content.Headers:");
            foreach (var header in response.RequestMessage.Content.Headers)
            {
                foreach (var value in header.Value)
                {
                    headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
                }
            }
        }
        if (response.Headers.TryGetValues("CF-RAY", out var rayValues))
        {
            headersBuilder.AppendLine("ResponseMessage.Headers.Cloudflare:");
            foreach (var value in rayValues)
            {
                headersBuilder.AppendLine($"\"Cf-Ray\": \"{value}\"");
            }
        }
        if (response.Headers.TryGetValues("CF-CACHE-STATUS", out var cacheValues))
        {
            headersBuilder.AppendLine("ResponseMessage.Headers.Cloudflare:");
            foreach (var value in cacheValues)
            {
                headersBuilder.AppendLine($"\"Cf-Cache-Status\": \"{value}\"");
            }
        }
        return headersBuilder.ToString();
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(Uri requestUrl, string fileHash, CancellationToken ct, bool withToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await _orchestrator.SendRequestAsync(HttpMethod.Get, requestUrl, ct, HttpCompletionOption.ResponseHeadersRead, withToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // Dump some helpful debugging info
                    string responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    Logger.LogWarning("Unsuccessful status code for {requestUrl} is {statusCode}, request headers: \n{headers}\n, response text: \n\"{responseText}\"", requestUrl, response.StatusCode, BuildHeaderDump(response), responseText);

                    // Raise an exception etc
                    response.EnsureSuccessStatusCode();
                }
                else if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("Successful response for {requestUrl} is {statusCode}, request headers: \n{headers}", requestUrl, response.StatusCode, BuildHeaderDump(response));
                }

                return response;
            }
            catch (HttpRequestException ex)
            {
                response?.Dispose();
                Logger.LogWarning(ex, "Error during download of {requestUrl}, HttpStatusCode: {code}", requestUrl, ex.StatusCode);
                if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                {
                    if (ex.StatusCode is HttpStatusCode.NotFound)
                        await ReportFileMissing(fileHash, ct).ConfigureAwait(false);

                    throw new InvalidDataException($"Http error {ex.StatusCode} (cancelled: {ct.IsCancellationRequested}): {requestUrl}", ex);
                }

                // 1225: a server error, timeout or rate limit (or a dropped connection before any body byte) is retried
                // twice with a short backoff before the file is given up for this round.
                if (attempt < MaxHttpRetries && (ex.StatusCode == null || IsRetryableStatus(ex.StatusCode)) && !ct.IsCancellationRequested)
                {
                    var delay = TimeSpan.FromMilliseconds(750 * (attempt + 1) + Random.Shared.Next(250));
                    Logger.LogInformation("{hash}: retrying download in {delay} ms (attempt {attempt} of {max})", fileHash, (int)delay.TotalMilliseconds, attempt + 2, MaxHttpRetries + 1);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                throw;
            }
            catch
            {
                response?.Dispose();
                throw;
            }
        }
    }

    private async Task<DownloadedPayload> DownloadFileThrottled(Uri requestUrl, string tempFilename, string fileHash, IProgress<long> progress, DownloadDataCallback? callback, CancellationToken ct, bool withToken = true)
    {
        long usageBytes = 0; // 1211: added to the usage graph once, when this transfer ends or is cancelled
        var response = await SendDownloadRequestAsync(requestUrl, fileHash, ct, withToken).ConfigureAwait(false);

        ThrottledStream? stream = null;
        byte[]? memory = null;
        long reserved = 0;
        bool toFile = false;
        try
        {
            var length = response.Content.Headers.ContentLength;
            if (length is > 0 and <= InMemoryFileLimit)
            {
                if (Interlocked.Add(ref s_inMemoryBytes, length.Value) <= InMemoryBudget)
                {
                    reserved = length.Value;
                    memory = new byte[length.Value];
                }
                else
                {
                    Interlocked.Add(ref s_inMemoryBytes, -length.Value);
                }
            }
            toFile = memory == null;

            var limit = _orchestrator.DownloadLimitFor(fileHash);
            Logger.LogTrace("Starting Download with a speed limit of {limit} to {target}", limit, toFile ? tempFilename : "memory");
            stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit) { Tag = fileHash };

            lock (_downloadInfoLock)
            {
                _activeDownloadStreams.Add(stream);
            }

            if (memory != null)
            {
                int offset = 0;
                int bytesRead;
                while (offset < memory.Length
                    && (bytesRead = await stream.ReadAsync(memory.AsMemory(offset, Math.Min(MaxReadSlice, memory.Length - offset)), ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    callback?.Invoke(memory.AsSpan(offset, bytesRead));
                    offset += bytesRead;
                    progress.Report(bytesRead);
                    usageBytes += bytesRead;
                }

                if (offset != memory.Length)
                {
                    throw new InvalidDataException($"Download of {fileHash} ended early: {offset} of {memory.Length} bytes");
                }

                Logger.LogDebug("{requestUrl} downloaded to memory ({bytes} bytes)", requestUrl, memory.Length);
                return new DownloadedPayload { Memory = memory, ReservedBytes = reserved };
            }

            var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
            try
            {
                var fileStream = LowPriorityFile.Create(tempFilename); // fork: low-priority I/O (Wine)
                await using (fileStream.ConfigureAwait(false))
                {
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, StreamBufferSize), ct).ConfigureAwait(false)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        callback?.Invoke(buffer.AsSpan(0, bytesRead));
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                        progress.Report(bytesRead);
                        usageBytes += bytesRead;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            Logger.LogDebug("{requestUrl} downloaded to {tempPath}", requestUrl, tempFilename);
            return new DownloadedPayload { TempFile = tempFilename };
        }
        catch (Exception ex)
        {
            if (reserved > 0) Interlocked.Add(ref s_inMemoryBytes, -reserved);
            if (toFile && ex is not OperationCanceledException)
            {
                try
                {
                    if (!tempFilename.IsNullOrEmpty())
                        File.Delete(tempFilename);
                }
                catch
                {
                    // ignore if file deletion fails
                }
            }
            throw;
        }
        finally
        {
            MareSynchronos.Services.Usage.TransferUsage.AddDownload(usageBytes);
            if (stream != null)
            {
                lock (_downloadInfoLock)
                {
                    _activeDownloadStreams.Remove(stream);
                }

                await stream.DisposeAsync().ConfigureAwait(false);
            }

            response.Dispose();
        }
    }

    // 1.15.5.1403 DownloadPipeline legacy: the 1224 transfer (temp file only, no HTTP retry), verbatim.
    private async Task DownloadFileThrottledLegacy(Uri requestUrl, string destinationFilename, string fileHash, IProgress<long> progress, DownloadDataCallback? callback, CancellationToken ct, bool withToken = true)
    {
        HttpResponseMessage response = null!;
        long usageBytes = 0; // 1211: added to the usage graph once, when this transfer ends or is cancelled
        try
        {
            response = await _orchestrator.SendRequestAsync(HttpMethod.Get, requestUrl, ct, HttpCompletionOption.ResponseHeadersRead, withToken).ConfigureAwait(false);

            var headersBuilder = new StringBuilder();
            if (response.RequestMessage != null)
            {
                headersBuilder.AppendLine("DefaultRequestHeaders:");
                foreach (var header in _orchestrator.DefaultRequestHeaders)
                {
                    foreach (var value in header.Value)
                    {
                        headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
                    }
                }
                headersBuilder.AppendLine("RequestMessage.Headers:");
                foreach (var header in response.RequestMessage.Headers)
                {
                    foreach (var value in header.Value)
                    {
                        headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
                    }
                }
                if (response.RequestMessage.Content != null)
                {
                    headersBuilder.AppendLine("RequestMessage.Content.Headers:");
                    foreach (var header in response.RequestMessage.Content.Headers)
                    {
                        foreach (var value in header.Value)
                        {
                            headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
                        }
                    }
                }
                if (response.Headers.TryGetValues("CF-RAY", out var rayValues))
                {
                    headersBuilder.AppendLine("ResponseMessage.Headers.Cloudflare:");
                    foreach (var value in rayValues)
                    {
                        headersBuilder.AppendLine($"\"Cf-Ray\": \"{value}\"");
                    }
                }
                if (response.Headers.TryGetValues("CF-CACHE-STATUS", out var cacheValues))
                {
                    headersBuilder.AppendLine("ResponseMessage.Headers.Cloudflare:");
                    foreach (var value in cacheValues)
                    {
                        headersBuilder.AppendLine($"\"Cf-Cache-Status\": \"{value}\"");
                    }
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                // Dump some helpful debugging info
                string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Logger.LogWarning("Unsuccessful status code for {requestUrl} is {statusCode}, request headers: \n{headers}\n, response text: \n\"{responseText}\"", requestUrl, response.StatusCode, headersBuilder.ToString(), responseText);

                // Raise an exception etc
                response.EnsureSuccessStatusCode();
            }
            else
            {
                Logger.LogDebug("Successful response for {requestUrl} is {statusCode}, request headers: \n{headers}", requestUrl, response.StatusCode, headersBuilder.ToString());
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Error during download of {requestUrl}, HttpStatusCode: {code}", requestUrl, ex.StatusCode);
            if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                if (ex.StatusCode is HttpStatusCode.NotFound)
                    await ReportFileMissing(fileHash, ct).ConfigureAwait(false);

                throw new InvalidDataException($"Http error {ex.StatusCode} (cancelled: {ct.IsCancellationRequested}): {requestUrl}", ex);
            }

            throw;
        }

        ThrottledStream? stream = null;
        try
        {
            var fileStream = LowPriorityFile.Create(destinationFilename); // fork: low-priority I/O (Wine)
            await using (fileStream.ConfigureAwait(false))
            {
                var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 65536 : 8196;
                var buffer = new byte[bufferSize];

                var bytesRead = 0;
                var limit = _orchestrator.DownloadLimitFor(fileHash);
                Logger.LogTrace("Starting Download with a speed limit of {limit} to {tempPath}", limit, destinationFilename);
                stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit) { Tag = fileHash };

                lock (_downloadInfoLock)
                {
                    _activeDownloadStreams.Add(stream);
                }

                while ((bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    if (callback != null)
                    {
                        callback.Invoke(buffer.AsSpan(0, bytesRead));
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);

                    progress.Report(bytesRead);
                    usageBytes += bytesRead;
                }

                Logger.LogDebug("{requestUrl} downloaded to {tempPath}", requestUrl, destinationFilename);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                if (!destinationFilename.IsNullOrEmpty())
                    File.Delete(destinationFilename);
            }
            catch
            {
                // ignore if file deletion fails
            }
            throw;
        }
        finally
        {
            MareSynchronos.Services.Usage.TransferUsage.AddDownload(usageBytes);
            if (stream != null)
            {
                lock (_downloadInfoLock)
                {
                    _activeDownloadStreams.Remove(stream);
                }

                await stream.DisposeAsync().ConfigureAwait(false);
            }

            response?.Dispose();
        }
    }

    // - locallyPresentFiles has hashes in the fileReplacement list that shouldn't be downloaded (e.g. if there is no comp alt)
    // - writes into compressedSubstitutions what substitutions were made, key: uncompressed hash, value: compressed hash
    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(string debugName, List<string> fileHashes, CompressedAlternateUsage compressedAlternateUsage, Dictionary<string, string> compressedSubstitutions, HashSet<string> locallyPresentFiles, int storeId, CancellationToken ct)
    {
        if (fileHashes.Count == 0)
        {
            return CurrentDownloads;
        }

        Logger.LogDebug("Download start: {id}", debugName);

        List<DownloadFileDto> downloadFileInfoFromService =
        [
            .. await FilesGetSizes(fileHashes, storeId, ct).ConfigureAwait(false),
        ];

        // Security fix: every hash the server sends ends up in a file path; drop entries with a malformed hash.
        var invalidHashCount = downloadFileInfoFromService.RemoveAll(f => !FileCacheManager.IsValidFileHash(f.Hash)
            || (f.CompressedAlternateFileDownload != null && !FileCacheManager.IsValidFileHash(f.CompressedAlternateFileDownload.Hash)));
        if (invalidHashCount > 0)
        {
            Logger.LogWarning("Skipped {count} download entries with an invalid file hash from the server for {id}", invalidHashCount, debugName);
        }

        Logger.LogDebug("Files with size 0 or less: {files}", string.Join(", ", downloadFileInfoFromService.Where(f => f.Size <= 0).Select(f => f.Hash)));

        foreach (var dto in downloadFileInfoFromService.Where(c => c.IsForbidden))
        {
            if (!_orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, dto.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new DownloadFileTransfer(dto));
            }
        }

        for (int i = 0; i < downloadFileInfoFromService.Count; i++)
        {
            var file = downloadFileInfoFromService[i];

            _compressedAlternateManager.SetCompressedAlternate(file.Hash, file.CompressedAlternateFileDownload?.Hash, file.WillNotBeCompressed);
            bool usingAlternate = false;
            if (file.CompressedAlternateFileDownload != null)
            {
                // Assume that compressed alternates do not themselves have a compressed alternate
                _compressedAlternateManager.SetCompressedAlternate(file.CompressedAlternateFileDownload.Hash, compressedAlternateHash: null, neverWillHaveAlternate: true);

                // If the compressed alternate usage requests it, download the alternate instead.
                if (compressedAlternateUsage == CompressedAlternateUsage.CompressedNewDownloads || compressedAlternateUsage == CompressedAlternateUsage.AlwaysCompressed)
                {
                    usingAlternate = true;
                    Logger.LogDebug("Preparing to download compressed file {compressed} instead of original file {original}.", file.CompressedAlternateFileDownload.Hash, file.Hash);
                    downloadFileInfoFromService[i] = file.CompressedAlternateFileDownload;
                    compressedSubstitutions[file.Hash] = file.CompressedAlternateFileDownload.Hash;

                    //if (!locallyPresentFiles.Contains(file.CompressedAlternateFileDownload.Hash) && _fileDbManager.GetFirstFileCacheEntityByHash(file.CompressedAlternateFileDownload.Hash) != null)
                    //{
                    //    Logger.LogDebug("But compressed alternate {file} is already downloaded! Not downloading again.", file.CompressedAlternateFileDownload.Hash);
                    //    locallyPresentFiles.Add(file.CompressedAlternateFileDownload.Hash);
                    //}
                    if (!locallyPresentFiles.Contains(file.CompressedAlternateFileDownload.Hash) 
                        && (await _fileDbManager.GetFirstFileCacheEntityByHash(file.CompressedAlternateFileDownload.Hash, ct).ConfigureAwait(false)) != null)
                    {
                        Logger.LogDebug("But compressed alternate {file} is already downloaded! Not downloading again.", file.CompressedAlternateFileDownload.Hash);
                        locallyPresentFiles.Add(file.CompressedAlternateFileDownload.Hash);
                    }
                }
            }

            // If we were asked to download a locally present file, we were just checking if it has an alternate; no need to redownload it.
            // If we were asked to download a file and we will use its compressed alternate which is already present then no need to redownload it.
            if ((locallyPresentFiles.Contains(file.Hash) && !usingAlternate)
                || (usingAlternate && locallyPresentFiles.Contains(downloadFileInfoFromService[i].Hash)))
            {
                downloadFileInfoFromService.RemoveAt(i);
                i--;
            }
        }

        var allDownloads = downloadFileInfoFromService.Distinct().Select(d => new DownloadFileTransfer(d))
            .Where(d => d.CanBeTransferred).ToList();

        foreach (var download in allDownloads)
        {
            // ok, listen, the timing on some of this is so tight, this check is actually needed
            // the 60-90ms timing is real between different pairs and when they get the response for FilesGetSizes
            // someone else may have already downloaded the file before they even got here
            if (await _fileDbManager.GetFirstFileCacheEntityByHash(download.Hash, ct).ConfigureAwait(false) != null)
            {
                continue;
            }

            CurrentDownloads.Add(download);
        }

        return CurrentDownloads;
    }

    private async Task DownloadFilesInternal(DownloadBatchInfo batchInfo, List<FileReplacementData> fileReplacement, Dictionary<string, string> compressionSubstitutions, CancellationToken ct)
    {
        // Separate out the files with direct download URLs
        var directDownloads = CurrentDownloads.Where(download => !string.IsNullOrEmpty(download.DirectDownloadUrl)).ToList();

        // Create download status trackers for the direct downloads
        foreach (var directDownload in directDownloads)
        {
            _downloadStatus[directDownload.DirectDownloadUrl!] = new FileDownloadStatus()
            {
                DownloadStatus = DownloadStatus.Initializing,
                TotalBytes = directDownload.Total,
                TotalFiles = 1,
                TransferredBytes = 0,
                TransferredFiles = 0
            };
        }

        Logger.LogInformation("Downloading {direct} files directly.", directDownloads.Count);
        if (directDownloads.Count < CurrentDownloads.Count)
        {
            Logger.LogWarning("NOTE: {legacy} files did not have direct download URLs and cannot be downloaded.", CurrentDownloads.Count - directDownloads.Count);
        }

        Mediator.Publish(new DownloadStartedMessage(batchInfo, _downloadStatus));

        // Fork: priority tier of this batch (UID list, in-game friends, direct syncs, everyone else)
        var batchTier = _orchestrator.GetTier(batchInfo);
        Logger.LogInformation("Priority tier {tier} ({tierName}) for {name} ({uid}): {count} file(s)", batchTier, PriorityTiers.Name(batchTier),
            batchInfo.DisplayName, batchInfo.Pair?.UserData.AliasOrUID ?? "-", directDownloads.Count);

        // 1225: one lookup table instead of a linear search over every replacement for each file (new pipeline only).
        Dictionary<string, FileReplacementData>? replacementByDownloadHash = null;
        // 1.15.5.1410: new (1225+) download pipeline only; the DownloadPipeline switch is gone.
        {
            replacementByDownloadHash = new Dictionary<string, FileReplacementData>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fileReplacement)
            {
                if (f.GamePaths == null || !f.GamePaths.Any()) continue;
                replacementByDownloadHash.TryAdd(compressionSubstitutions.GetValueOrDefault(f.Hash, f.Hash), f);
            }
        }

        // 1225: bounded per-batch parallelism (was one task per file). The global slot limit still decides how many
        // transfers really run; this only stops a big batch from opening every file and socket at the same moment.
        // Under Wine a batch is fed a few files at a time and file starts are spaced out a little.
        var configured = _orchestrator.ConfiguredParallelDownloads;
        var parallelism = LowPriorityFile.IsWine ? Math.Clamp(configured, 2, 6) : Math.Clamp(configured + 2, 4, 16);
        parallelism = Math.Max(1, Math.Min(parallelism, directDownloads.Count));
        var startSpacing = LowPriorityFile.IsWine && directDownloads.Count > parallelism ? TimeSpan.FromMilliseconds(15) : TimeSpan.Zero;
        // 1228 DownloadCaps legacy (pre-1225, read once per batch): one task per file at once, no start spacing.

        var pacerLock = new object();
        var nextStart = DateTime.UtcNow;
        if (directDownloads.Count > parallelism)
        {
            Logger.LogDebug("Batch {name}: {count} files, {parallel} at a time", batchInfo.DisplayName, directDownloads.Count, parallelism);
        }

        // Start downloading each of the direct downloads
        var directDownloadsTask = directDownloads.Count == 0 ? Task.CompletedTask : Parallel.ForEachAsync(directDownloads, new ParallelOptions()
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = ct,
        },
        async (directDownload, token) =>
        {
            if (!_downloadStatus.TryGetValue(directDownload.DirectDownloadUrl!, out var downloadTracker) || downloadTracker == null)
                return;

            if (startSpacing > TimeSpan.Zero)
            {
                TimeSpan wait;
                lock (pacerLock)
                {
                    var now = DateTime.UtcNow;
                    if (nextStart < now) nextStart = now;
                    wait = nextStart - now;
                    nextStart += startSpacing;
                }
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            }

            await _orchestrator.JoinOrStartDownloadAsync(directDownload.Hash,
                (_, bytesDownloaded) => downloadTracker.TransferredBytes += bytesDownloaded,
                (status) => downloadTracker.DownloadStatus = status,
                async (hash, progress, statusCallback, transferToken) =>
                {
                    if (!FileCacheManager.IsValidFileHash(directDownload.Hash))
                    {
                        Logger.LogWarning("Skipping direct download with an invalid file hash");
                        RemoveDownload(directDownload);
                        return;
                    }

                    try
                    {
                        statusCallback.Invoke(DownloadStatus.WaitingForSlot);
                        await _orchestrator.WaitForDownloadSlotAsync(directDownload.Hash, batchTier, transferToken, batchInfo.Pair).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.LogDebug("{hash}: Detected cancellation of file queued for direct download, canceling.", directDownload.Hash);
                        RemoveDownload(directDownload);
                        return;
                    }

                    var tempFilename = _fileDbManager.GetCacheTempFilePath(directDownload.Hash);
                    DownloadedPayload payload;
                    try
                    {
                        // 1226: Wine-only per-second cap on new transfers (slot already held; cancellation releases it below)
                        await WineFileStartPaceAsync(Logger, transferToken).ConfigureAwait(false);

                        // Download the compressed file directly
                        statusCallback.Invoke(DownloadStatus.Downloading);

                        DownloadDataCallback? munge = null;
                        if (directDownload.MungeKey != null)
                        {
                            long mungeOffset = 0;
                            byte[] mungeKeyBytes = Encoding.ASCII.GetBytes(directDownload.MungeKey);
                            munge = span =>
                            {
                                for (int i = 0; i < span.Length; i++)
                                {
                                    span[i] = (byte)(span[i] ^ mungeKeyBytes[mungeOffset]);
                                    mungeOffset = (mungeOffset + 1) % mungeKeyBytes.Length;
                                }
                            };
                        }
#if DEBUG
                        Logger.LogDebug("Beginning direct download of {hash} from {url} with key {key}", directDownload.Hash, directDownload.DirectDownloadUrl!, directDownload.MungeKey ?? "<none>");
#else
                        Logger.LogDebug("Beginning direct download of {hash} from {url}", directDownload.Hash, directDownload.DirectDownloadUrl!);
#endif
                        payload = await DownloadFileThrottled(new Uri(directDownload.DirectDownloadUrl!), tempFilename, directDownload.Hash, progress, munge, transferToken, withToken: false).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex)
                    {
                        Logger.LogDebug("{hash}: Detected cancellation of direct download, discarding file.", directDownload.Hash);
                        _orchestrator.ReleaseDownloadSlot(directDownload.Hash);
                        DeleteTemporaryDownloadFile(tempFilename);
                        Logger.LogDebug(ex, "{hash}: Error during direct download.", directDownload.Hash);
                        RemoveDownload(directDownload);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _orchestrator.ReleaseDownloadSlot(directDownload.Hash);
                        DeleteTemporaryDownloadFile(tempFilename);
                        Logger.LogError(ex, "{hash}: Error during direct download.", directDownload.Hash);
                        RemoveDownload(directDownload);
                        return;
                    }

                    // 1225: the network slot is free as soon as the bytes are in; the cache write below has its own gate.
                    _orchestrator.ReleaseDownloadSlot(directDownload.Hash);

                    // Decompress from the download to the final file
                    statusCallback.Invoke(DownloadStatus.Decompressing);

                    try
                    {
                        if (!replacementByDownloadHash!.TryGetValue(directDownload.Hash, out var replacementForHash))
                        {
                            throw new InvalidOperationException($"No file replacement for downloaded hash {directDownload.Hash}");
                        }
                        var fileExtension = replacementForHash.GamePaths[0].Split(".")[^1].ToLowerInvariant();
                        // Security fix: the extension comes from the pair's game path; never let it reach the file system unchecked.
                        var origin = MareSynchronos.Services.PSyncFileSafety.OriginOf(directDownload.DirectDownloadUrl);
                        MareSynchronos.Services.PSyncFileSafety.RecordOrigin(batchInfo.Pair?.UserData.UID, origin);
                        // 1.15.5.1217: only known game mod file types are written to the cache (allowlist).
                        if (!FileCacheManager.IsSafeCacheExtension(fileExtension) || !MareSynchronos.Services.PSyncFileSafety.IsAllowedExtension(fileExtension))
                        {
                            Logger.LogWarning("PSync file safety: BLOCKED {hash} from {origin} for {pair}: file type '.{ext}' is not an allowed game mod type",
                                directDownload.Hash, origin, batchInfo.Pair?.UserData.AliasOrUID ?? "unknown", FileCacheManager.IsSafeCacheExtension(fileExtension) ? fileExtension : "(invalid)");
                            // Per pair only (audit M1): the type comes from this pair's game path, not from the file bytes.
                            MareSynchronos.Services.PSyncFileSafety.CountBlocked();
                            MareSynchronos.Services.PSyncFileSafety.RefuseForPair(batchInfo.Pair?.UserData.UID, directDownload.Hash);
                            return;
                        }
                        var finalFilename = _fileDbManager.GetCacheFilePath(directDownload.Hash, fileExtension);
                        Logger.LogDebug("Decompressing direct download {hash} to {finalFile}", directDownload.Hash, finalFilename);
                        var stored = await Task.Run(() => DecompressVerifyAndWriteAsync(directDownload, payload, finalFilename, origin, fileExtension, batchInfo), CancellationToken.None).ConfigureAwait(false);
                        if (stored != null && await PersistFileToStorageAsync(directDownload.Hash, finalFilename, stored.Value.Size, stored.Value.LastWriteUtc).ConfigureAwait(false))
                        {
                            Logger.LogDebug("Finished direct download of {hash}.", directDownload.Hash);
                        }
                    }
                    // Fork: the bytes are already in, so the decompress and cache write above run to the end even if the
                    // batch is cancelled (no token), so the file is not fetched twice. A cancel PSync caused itself is not an error.
                    catch (OperationCanceledException ex)
                    {
                        Logger.LogDebug(ex, "Cache write of {hash} was cancelled", directDownload.Hash);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Exception downloading {hash} from {url}", directDownload.Hash, directDownload.DirectDownloadUrl!);
                    }
                    finally
                    {
                        payload.ReleaseReservation();
                        if (payload.TempFile != null) DeleteTemporaryDownloadFile(payload.TempFile);
                    }

                }, token, batchTier, batchInfo.Pair).ConfigureAwait(false);
            downloadTracker.TransferredFiles = 1;
        });

        // Wait for all the direct downloads to complete
        await directDownloadsTask.ConfigureAwait(false);

        Logger.LogDebug("Download end: {id}", batchInfo.DisplayName);

        ClearDownload();
    }

    /// <summary>
    /// 1.15.5.1403 DownloadPipeline legacy: one direct download exactly as in 1.15.5.1224 (55051917). The download slot is held
    /// through the transfer, read-back, decompress, hash and write; the bytes go to the temp file only (no in-memory buffer);
    /// decompress runs inline (no shared finalize gate); the DB row is saved per file; the batch token cancels at once.
    /// </summary>
    private async Task DownloadDirectFileLegacyAsync(DownloadFileTransfer directDownload, IProgress<long> progress, Action<DownloadStatus> statusCallback,
        int batchTier, DownloadBatchInfo batchInfo, List<FileReplacementData> fileReplacement, Dictionary<string, string> compressionSubstitutions, CancellationToken token)
    {
        if (!FileCacheManager.IsValidFileHash(directDownload.Hash))
        {
            Logger.LogWarning("Skipping direct download with an invalid file hash");
            RemoveDownload(directDownload);
            return;
        }

        try
        {
            statusCallback.Invoke(DownloadStatus.WaitingForSlot);
            await _orchestrator.WaitForDownloadSlotAsync(directDownload.Hash, batchTier, token, batchInfo.Pair).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("{hash}: Detected cancellation of file queued for direct download, canceling.", directDownload.Hash);
            RemoveDownload(directDownload);
            return;
        }

        var tempFilename = _fileDbManager.GetCacheTempFilePath(directDownload.Hash);
        try
        {
            // 1226 WineStartCap: off since 1.15.5.1414 (WineStartCapEnabled = false), so this returns at once on every platform.
            await WineFileStartPaceAsync(Logger, token).ConfigureAwait(false);

            // Download the compressed file directly
            statusCallback.Invoke(DownloadStatus.Downloading);

            DownloadDataCallback? munge = null;
            if (directDownload.MungeKey != null)
            {
                long mungeOffset = 0;
                byte[] mungeKeyBytes = Encoding.ASCII.GetBytes(directDownload.MungeKey);
                munge = span =>
                {
                    for (int i = 0; i < span.Length; i++)
                    {
                        span[i] = (byte)(span[i] ^ mungeKeyBytes[mungeOffset]);
                        mungeOffset = (mungeOffset + 1) % mungeKeyBytes.Length;
                    }
                };
            }
#if DEBUG
            Logger.LogDebug("Beginning direct download of {hash} from {url} with key {key}", directDownload.Hash, directDownload.DirectDownloadUrl!, directDownload.MungeKey ?? "<none>");
#else
            Logger.LogDebug("Beginning direct download of {hash} from {url}", directDownload.Hash, directDownload.DirectDownloadUrl!);
#endif
            await DownloadFileThrottledLegacy(new Uri(directDownload.DirectDownloadUrl!), tempFilename, directDownload.Hash, progress, munge, token, withToken: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            Logger.LogDebug("{hash}: Detected cancellation of direct download, discarding file.", directDownload.Hash);
            _orchestrator.ReleaseDownloadSlot(directDownload.Hash);
            DeleteTemporaryDownloadFile(tempFilename);
            Logger.LogDebug(ex, "{hash}: Error during direct download.", directDownload.Hash);
            RemoveDownload(directDownload);
            return;
        }
        catch (Exception ex)
        {
            _orchestrator.ReleaseDownloadSlot(directDownload.Hash);
            DeleteTemporaryDownloadFile(tempFilename);
            Logger.LogError(ex, "{hash}: Error during direct download.", directDownload.Hash);
            RemoveDownload(directDownload);
            return;
        }

        // Decompress from tempFilename to finalFilename
        statusCallback.Invoke(DownloadStatus.Decompressing);

        try
        {
            var fileExtension = fileReplacement.First(f => string.Equals(compressionSubstitutions.GetValueOrDefault(f.Hash, f.Hash), directDownload.Hash, StringComparison.OrdinalIgnoreCase)).GamePaths[0].Split(".")[^1].ToLowerInvariant();
            // Security fix: the extension comes from the pair's game path; never let it reach the file system unchecked.
            var origin = MareSynchronos.Services.PSyncFileSafety.OriginOf(directDownload.DirectDownloadUrl);
            MareSynchronos.Services.PSyncFileSafety.RecordOrigin(batchInfo.Pair?.UserData.UID, origin);
            // 1.15.5.1217: only known game mod file types are written to the cache (allowlist).
            if (!FileCacheManager.IsSafeCacheExtension(fileExtension) || !MareSynchronos.Services.PSyncFileSafety.IsAllowedExtension(fileExtension))
            {
                Logger.LogWarning("PSync file safety: BLOCKED {hash} from {origin} for {pair}: file type '.{ext}' is not an allowed game mod type",
                    directDownload.Hash, origin, batchInfo.Pair?.UserData.AliasOrUID ?? "unknown", FileCacheManager.IsSafeCacheExtension(fileExtension) ? fileExtension : "(invalid)");
                // Per pair only (audit M1): the type comes from this pair's game path, not from the file bytes.
                MareSynchronos.Services.PSyncFileSafety.CountBlocked();
                MareSynchronos.Services.PSyncFileSafety.RefuseForPair(batchInfo.Pair?.UserData.UID, directDownload.Hash);
                return;
            }
            var finalFilename = _fileDbManager.GetCacheFilePath(directDownload.Hash, fileExtension);
            Logger.LogDebug("Decompressing direct download {hash} from {compressedFile} to {finalFile}", directDownload.Hash, tempFilename, finalFilename);
            byte[] compressedBytes = await LowPriorityFile.ReadAllBytesAsync(tempFilename, CancellationToken.None).ConfigureAwait(false);
            var decompressedBytes = LZ4Wrapper.Unwrap(compressedBytes);
            var downloadedHash = Convert.ToHexString(SHA1.HashData(decompressedBytes));
            if (!string.Equals(downloadedHash, directDownload.Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Downloaded file hash mismatch: expected {directDownload.Hash}, got {downloadedHash}");
            }
            if (MareSynchronos.Services.PSyncFileSafety.LooksExecutable(decompressedBytes))
            {
                Logger.LogWarning("PSync file safety: BLOCKED {hash} from {origin} for {pair}: content looks like a program or archive, not a '.{ext}' game file",
                    directDownload.Hash, origin, batchInfo.Pair?.UserData.AliasOrUID ?? "unknown", fileExtension);
                PSyncForbid(directDownload);
                return;
            }
            await _fileCompactor.WriteAllBytesAsync(finalFilename, decompressedBytes, CancellationToken.None).ConfigureAwait(false);
            if (await PersistFileToStorageLegacyAsync(downloadedHash, finalFilename, CancellationToken.None).ConfigureAwait(false))
            {
                Logger.LogDebug("Finished direct download of {hash}.", directDownload.Hash);
            }
        }
        // Fork: the bytes are already on disk, so the decompress and cache write above run to the end even if the
        // batch is cancelled (no token), so the file is not fetched twice. A cancel PSync caused itself is not an error.
        catch (OperationCanceledException ex)
        {
            Logger.LogDebug(ex, "Cache write of {hash} was cancelled", directDownload.Hash);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Exception downloading {hash} from {url}", directDownload.Hash, directDownload.DirectDownloadUrl!);
        }
        finally
        {
            _orchestrator.ReleaseDownloadSlot(directDownload.Hash);
            DeleteTemporaryDownloadFile(tempFilename);
        }
    }

    /// <summary>
    /// 1225: decompress, hash check, safety check and the cache write as one step behind a small gate
    /// (1 at a time under Wine, 3 elsewhere), always on a pool thread. Returns the size and last-write time
    /// stamped on the file, or null when the file was blocked.
    /// </summary>
    private async Task<(long Size, DateTime LastWriteUtc)?> DecompressVerifyAndWriteAsync(DownloadFileTransfer directDownload, DownloadedPayload payload, string finalFilename, string origin, string fileExtension, DownloadBatchInfo batchInfo)
    {
        await s_finalizeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[] compressedBytes = payload.Memory ?? await LowPriorityFile.ReadAllBytesAsync(payload.TempFile!, CancellationToken.None).ConfigureAwait(false);
            var decompressedBytes = LZ4Wrapper.Unwrap(compressedBytes);
            compressedBytes = null!;
            payload.ReleaseReservation();
            var downloadedHash = Convert.ToHexString(SHA1.HashData(decompressedBytes));
            if (!string.Equals(downloadedHash, directDownload.Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Downloaded file hash mismatch: expected {directDownload.Hash}, got {downloadedHash}");
            }
            if (MareSynchronos.Services.PSyncFileSafety.LooksExecutable(decompressedBytes))
            {
                Logger.LogWarning("PSync file safety: BLOCKED {hash} from {origin} for {pair}: content looks like a program or archive, not a '.{ext}' game file",
                    directDownload.Hash, origin, batchInfo.Pair?.UserData.AliasOrUID ?? "unknown", fileExtension);
                PSyncForbid(directDownload);
                return null;
            }

            var creationUtc = RandomDayInThePast();
            var lastWriteUtc = RandomDayInThePast();
            await _fileCompactor.WriteAllBytesWithTimesAsync(finalFilename, decompressedBytes, creationUtc, DateTime.UtcNow, lastWriteUtc, CancellationToken.None).ConfigureAwait(false);
            return (decompressedBytes.LongLength, lastWriteUtc);
        }
        finally
        {
            s_finalizeGate.Release();
        }
    }

    private static DateTime RandomDayInThePast()
    {
        DateTime start = new(1995, 1, 1, 1, 1, 1, DateTimeKind.Utc);
        int range = (DateTime.UtcNow.Date - start.Date).Days;

        return start.AddDays(Random.Shared.Next(range));
    }

    private void DeleteTemporaryDownloadFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Could not delete temporary download file {path}", path);
        }
    }

    private async Task<List<DownloadFileDto>> FilesGetSizes(List<string> hashes, int storeId, CancellationToken ct)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");
        var response = await _orchestrator.SendRequestAsync(HttpMethod.Get, MareFiles.ServerFilesGetSizesFullPath(_orchestrator.FilesCdnUri!, _orchestrator.TimeZoneUtcOffsetMinutes, storeId), hashes, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DownloadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private async Task ReportFileMissing(string hash, CancellationToken ct)
    {
        if (!_orchestrator.TryAddHashReportedError404(hash))
        {
            return; // another manager has already reported this hash missing
        }

        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");

        hash = hash.ToUpperInvariant();
        Logger.LogWarning("Download reported a 404 error for hash {hash}, reporting it to the FileServer", hash);

        await _orchestrator.SendRequestAsync(HttpMethod.Post, MareFiles.ServerFilesReportFile(_orchestrator.FilesCdnUri!), hash, ct).ConfigureAwait(false);
    }

    private void PSyncForbid(FileTransfer transfer)
    {
        MareSynchronos.Services.PSyncFileSafety.CountBlocked();
        lock (_orchestrator.ForbiddenTransfers)
        {
            if (!_orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, transfer.Hash, StringComparison.OrdinalIgnoreCase)))
                _orchestrator.ForbiddenTransfers.Add(transfer);
        }
    }

    private async Task<bool> PersistFileToStorageAsync(string fileHash, string filePath, long size, DateTime lastWriteUtc)
    {
        try
        {
            // 1225: times were stamped on the write handle and the size is known, so no stat or reopen here;
            // the DB row is saved together with other files that finished at the same time.
            var entry = await _fileDbManager.PersistVerifiedDownloadAsync(filePath, fileHash, size, lastWriteUtc).ConfigureAwait(false);
            if (entry == null)
            {
                Logger.LogError("Could not create or update the file cache for {path}", filePath);

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating file cache");

            return false;
        }
    }

    // 1.15.5.1403 DownloadPipeline legacy: the 1224 per-file DB save (stat, set times, one commit per file), verbatim.
    private async Task<bool> PersistFileToStorageLegacyAsync(string fileHash, string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists)
            {
                Logger.LogError("Could not persist downloaded file {path} because it does not exist?", filePath);

                return false;
            }
            DateTime RandomDayInThePast()
            {
                DateTime start = new(1995, 1, 1, 1, 1, 1, DateTimeKind.Utc);
                int range = (DateTime.UtcNow.Date - start.Date).Days;

                return start.AddDays(Random.Shared.Next(range));
            }
            fi.CreationTimeUtc = RandomDayInThePast();
            fi.LastAccessTimeUtc = DateTime.UtcNow;
            fi.LastWriteTimeUtc = RandomDayInThePast();
            var entry = await _fileDbManager.UpdateOrCreateFileCacheEntityByPath(filePath, FileCacheStore.Cache, cancellationToken, forceUpdate: true, markAccessed: true, verifiedHash: fileHash).ConfigureAwait(false);
            if (entry == null)
            {
                Logger.LogError("Could not create or update the file cache for {path}", filePath);

                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger.LogDebug("File cache creation was cancelled");

            return false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating file cache");

            return false;
        }
    }
}
