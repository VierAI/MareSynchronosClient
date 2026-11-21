using MareSynchronos.MareConfiguration;
using MareSynchronos.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MareSynchronos.FileCache;

public sealed class FileCompactor : IDisposable
{
    // 1.15.6.215: newly downloaded cache files are not compacted right away. Compaction opens the file for writing, and a pair being
    // applied at the same moment can fail to load it (missing body parts). New files wait at least 60 s, until no pair apply has run
    // for 10 s, and are skipped while another program (the game) has them open. Mass compaction from Settings is unchanged.
    private static readonly TimeSpan NewFileCompactDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ApplyQuietTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InUseRetryDelay = TimeSpan.FromSeconds(60);
    private const int MaxInUseRetries = 10;
    private const int MaxDeferredFiles = 20000;
    private readonly ConcurrentDictionary<string, (DateTime DueUtc, int Tries)> _deferredCompaction = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _deferredTimer;
    private int _deferredRunning;

    public const uint FSCTL_DELETE_EXTERNAL_BACKING = 0x90314U;
    public const ulong WOF_PROVIDER_FILE = 2UL;

    private readonly ConcurrentDictionary<string, int> _clusterSizes;

    private readonly WOF_FILE_COMPRESSION_INFO_V1 _efInfo;
    private readonly ILogger<FileCompactor> _logger;

    private readonly MareConfigService _mareConfigService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly BackgroundDiskWorker _disk;

    public FileCompactor(ILogger<FileCompactor> logger, MareConfigService mareConfigService, DalamudUtilService dalamudUtilService, BackgroundDiskWorker disk)
    {
        _clusterSizes = new(StringComparer.Ordinal);
        _logger = logger;
        _mareConfigService = mareConfigService;
        _dalamudUtilService = dalamudUtilService;
        _disk = disk;
        _efInfo = new WOF_FILE_COMPRESSION_INFO_V1
        {
            Algorithm = CompressionAlgorithm.XPRESS8K,
            Flags = 0
        };
        _deferredTimer = new Timer(_ => ProcessDeferredCompaction(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        _deferredTimer.Dispose();
        _deferredCompaction.Clear();
    }

    private void ProcessDeferredCompaction()
    {
        if (_deferredCompaction.IsEmpty || Interlocked.Exchange(ref _deferredRunning, 1) == 1) return;
        try
        {
            if (MareSynchronos.PlayerData.Pairs.PairDataApplicationHandler.PSyncAnyApplyInFlight
                || MareSynchronos.PlayerData.Pairs.PairDataApplicationHandler.PSyncSinceLastApply < ApplyQuietTime)
                return;
            if (_dalamudUtilService.IsWine || !_mareConfigService.Current.UseCompactor)
            {
                _deferredCompaction.Clear();
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var (path, entry) in _deferredCompaction)
            {
                if (entry.DueUtc > now) continue;
                if (!_deferredCompaction.TryRemove(path, out _)) continue;
                var tries = entry.Tries;
                _ = _disk.RunIdleAsync(() =>
                {
                    // Re-check right before compacting: an apply may have started or finished since the timer ran
                    // (1.15.6.216, audit V3: the quiet time is checked again here too, per file).
                    if (MareSynchronos.PlayerData.Pairs.PairDataApplicationHandler.PSyncAnyApplyInFlight
                        || MareSynchronos.PlayerData.Pairs.PairDataApplicationHandler.PSyncSinceLastApply < ApplyQuietTime)
                    {
                        _deferredCompaction.TryAdd(path, (DateTime.UtcNow + ApplyQuietTime, tries));
                        return;
                    }
                    if (!File.Exists(path)) return;
                    if (IsFileInUse(path))
                    {
                        if (tries + 1 < MaxInUseRetries) _deferredCompaction.TryAdd(path, (DateTime.UtcNow + InUseRetryDelay, tries + 1));
                        else _logger.LogDebug("File {file} stayed in use, not compacting it", path);
                        return;
                    }
                    CompactFile(path);
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deferred compaction pass failed");
        }
        finally
        {
            Interlocked.Exchange(ref _deferredRunning, 0);
        }
    }

    // True if the file is open somewhere in a way the compaction open (read/write, sharing read/write) would collide with, e.g. the
    // game reading it without sharing write access, or a writer still busy. Same open mode as CompactFile; closed at once.
    private static bool IsFileInUse(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private enum CompressionAlgorithm
    {
        NO_COMPRESSION = -2,
        LZNT1 = -1,
        XPRESS4K = 0,
        LZX = 1,
        XPRESS8K = 2,
        XPRESS16K = 3
    }

    public bool MassCompactRunning { get; private set; } = false;

    public string Progress { get; private set; } = string.Empty;

    public void CompactStorage(bool compress)
    {
        MassCompactRunning = true;
        try
        {
            var allFiles = Directory.EnumerateFiles(_mareConfigService.Current.CacheFolder).ToList();
            var tasks = new Task[allFiles.Count];
            for (var i = 0; i < allFiles.Count; i++)
            {
                var index = i + 1;
                var path = allFiles[i];
                tasks[i] = _disk.RunIdleAsync(() =>
                {
                    Progress = $"{index}/{allFiles.Count}";
                    if (compress)
                        CompactFile(path);
                    else
                        DecompressFile(path);
                });
            }

            Task.WaitAll(tasks);
        }
        finally
        {
            MassCompactRunning = false;
            Progress = string.Empty;
        }
    }

    public long GetFileSizeOnDisk(FileInfo fileInfo, bool? isNTFS = null)
    {
        bool ntfs = isNTFS ?? string.Equals(new DriveInfo(fileInfo.Directory!.Root.FullName).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);

        if (_dalamudUtilService.IsWine || !ntfs) return fileInfo.Length;

        var clusterSize = GetClusterSize(fileInfo);
        if (clusterSize == -1) return fileInfo.Length;
        Marshal.SetLastPInvokeError(0); 
        var lowSize = GetCompressedFileSizeW(fileInfo.FullName, out var highSize);
        // something fails and we show huge storage sizes, need to skip on error
        // probably the use of DangerousGetHandle returning a stale ptr
        if (lowSize == uint.MaxValue && Marshal.GetLastPInvokeError() != 0)
        {
            return fileInfo.Length;
        }

        var size = ((long)highSize << 32) | lowSize;
        return ((size + clusterSize - 1) / clusterSize) * clusterSize;
    }

    public Task WriteAllBytesAsync(string filePath, byte[] decompressedFile, CancellationToken token)
    {
        return _disk.RunAsync(() => WriteAllBytes(filePath, decompressedFile), token);
    }

    /// <summary>1225: write plus the file times in one go on the disk thread (see LowPriorityFile.WriteAllBytesWithTimes).</summary>
    public Task WriteAllBytesWithTimesAsync(string filePath, byte[] decompressedFile, DateTime creationUtc, DateTime lastAccessUtc, DateTime lastWriteUtc, CancellationToken token)
    {
        return _disk.RunAsync(() =>
        {
            LowPriorityFile.WriteAllBytesWithTimes(filePath, decompressedFile, creationUtc, lastAccessUtc, lastWriteUtc);
            QueueCompaction(filePath);
        }, token);
    }

    private void QueueCompaction(string filePath)
    {
        if (_dalamudUtilService.IsWine || !_mareConfigService.Current.UseCompactor)
            return;

        DeferCompaction(filePath);
    }

    private void DeferCompaction(string filePath)
    {
        if (_deferredCompaction.Count >= MaxDeferredFiles) return; // the mass compaction in Settings still covers these
        _deferredCompaction[filePath] = (DateTime.UtcNow + NewFileCompactDelay, 0);
    }

    public void WriteAllBytes(string filePath, byte[] decompressedFile)
    {
        LowPriorityFile.WriteAllBytes(filePath, decompressedFile);

        if (_dalamudUtilService.IsWine || !_mareConfigService.Current.UseCompactor)
            return;

        // 1.15.6.215: deferred (see NewFileCompactDelay); the timer queues it as idle disk work later.
        DeferCompaction(filePath);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCompressedFileSizeW([In, MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
        [Out, MarshalAs(UnmanagedType.U4)] out uint lpFileSizeHigh);

    [DllImport("kernel32.dll", SetLastError = true, PreserveSig = true)]
    private static extern int GetDiskFreeSpaceW([In, MarshalAs(UnmanagedType.LPWStr)] string lpRootPathName,
           out uint lpSectorsPerCluster, out uint lpBytesPerSector, out uint lpNumberOfFreeClusters,
           out uint lpTotalNumberOfClusters);

    [DllImport("WoFUtil.dll")]
    private static extern int WofIsExternalFile([MarshalAs(UnmanagedType.LPWStr)] string Filepath, out int IsExternalFile, out uint Provider, out WOF_FILE_COMPRESSION_INFO_V1 Info, ref uint BufferLength);

    [DllImport("WofUtil.dll")]
    private static extern int WofSetFileDataLocation(IntPtr FileHandle, ulong Provider, IntPtr ExternalFileInfo, ulong Length);

    private void CompactFile(string filePath)
    {
        var fs = new DriveInfo(new FileInfo(filePath).Directory!.Root.FullName);
        bool isNTFS = string.Equals(fs.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        if (!isNTFS)
        {
            _logger.LogWarning("Drive for file {file} is not NTFS", filePath);
            return;
        }

        var fi = new FileInfo(filePath);
        var oldSize = fi.Length;
        var clusterSize = GetClusterSize(fi);

        if (oldSize < Math.Max(clusterSize, 8 * 1024))
        {
            _logger.LogDebug("File {file} is smaller than cluster size ({size}), ignoring", filePath, clusterSize);
            return;
        }

        if (!IsCompactedFile(filePath))
        {
            _logger.LogDebug("Compacting file to XPRESS8K: {file}", filePath);

            WOFCompressFile(filePath);

            var newSize = GetFileSizeOnDisk(fi);

            _logger.LogDebug("Compressed {file} from {orig}b to {comp}b", filePath, oldSize, newSize);
        }
        else
        {
            _logger.LogDebug("File {file} already compressed", filePath);
        }
    }

    private void DecompressFile(string path)
    {
        _logger.LogDebug("Removing compression from {file}", path);
        try
        {
            // Default FileStream is ReadWrite with Share.Read, which blocks the validation read.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                LowPriorityFile.TrySetVeryLow(fs.SafeFileHandle);
#pragma warning disable S3869 // "SafeHandle.DangerousGetHandle" should not be called
                var hDevice = fs.SafeFileHandle.DangerousGetHandle();
#pragma warning restore S3869 // "SafeHandle.DangerousGetHandle" should not be called
                _ = DeviceIoControl(hDevice, FSCTL_DELETE_EXTERNAL_BACKING, nint.Zero, 0, nint.Zero, 0, out _, nint.Zero);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error decompressing file {path}", path);
        }
    }

    private int GetClusterSize(FileInfo fi)
    {
        if (!fi.Exists) return -1;
        var root = fi.Directory?.Root.FullName.ToLower() ?? string.Empty;
        if (string.IsNullOrEmpty(root)) return -1;
        if (_clusterSizes.TryGetValue(root, out int value)) return value;
        _logger.LogDebug("Getting Cluster Size for {path}, root {root}", fi.FullName, root);
        int result = GetDiskFreeSpaceW(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _);
        if (result == 0) return -1;
        _clusterSizes[root] = (int)(sectorsPerCluster * bytesPerSector);
        _logger.LogDebug("Determined Cluster Size for root {root}: {cluster}", root, _clusterSizes[root]);
        return _clusterSizes[root];
    }

    private static bool IsCompactedFile(string filePath)
    {
        uint buf = 8;
        _ = WofIsExternalFile(filePath, out int isExtFile, out uint _, out var info, ref buf);
        if (isExtFile == 0) return false;
        return info.Algorithm == CompressionAlgorithm.XPRESS8K;
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private void WOFCompressFile(string path)
    {
        var efInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(_efInfo));
        Marshal.StructureToPtr(_efInfo, efInfoPtr, fDeleteOld: true);
        ulong length = (ulong)Marshal.SizeOf(_efInfo);
        try
        {
            // Club fix: a file that is still open elsewhere (game or a reader) is retried once after a short delay, then skipped.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    // Share the file so validation can read it while WOF still holds the handle.
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    {
                        LowPriorityFile.TrySetVeryLow(fs.SafeFileHandle);
#pragma warning disable S3869 // "SafeHandle.DangerousGetHandle" should not be called
                        var hFile = fs.SafeFileHandle.DangerousGetHandle();
#pragma warning restore S3869 // "SafeHandle.DangerousGetHandle" should not be called
                        if (fs.SafeFileHandle.IsInvalid)
                        {
                            _logger.LogWarning("Invalid file handle to {file}", path);
                        }
                        else
                        {
                            var ret = WofSetFileDataLocation(hFile, WOF_PROVIDER_FILE, efInfoPtr, length);
                            if (!(ret == 0 || ret == unchecked((int)0x80070158)))
                            {
                                _logger.LogWarning("Failed to compact {file}: {ret}", path, ret.ToString("X"));
                            }
                        }
                    }
                    break;
                }
                catch (IOException ex) when (attempt == 0 && IsSharingViolation(ex))
                {
                    Thread.Sleep(250);
                }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    _logger.LogDebug("Skipped compacting {path}: still in use", path);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error compacting file {path}", path);
        }
        finally
        {
            Marshal.FreeHGlobal(efInfoPtr);
        }
    }

    private struct WOF_FILE_COMPRESSION_INFO_V1
    {
        public CompressionAlgorithm Algorithm;
        public ulong Flags;
    }
}
