using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace MareSynchronos.FileCache;

/// <summary>
/// File helpers whose reads and writes are issued at very low I/O priority and in small chunks,
/// so the game's normal-priority reads can be served while a download is hitting the disk.
/// </summary>
public static class LowPriorityFile
{
    private const int FileIoPriorityHintInfoClass = 12;
    private const int IoPriorityHintVeryLow = 0;
    private const int ChunkSize = 64 * 1024;
    // 1225: under Wine every write is a separate host call, so cache writes go out in 256 KB pieces there.
    // Windows keeps 64 KB pieces so the game's own reads still slot in between (unchanged).
    private const int WineWriteChunkSize = 256 * 1024;

    // Under Wine (macOS/Linux) the overlapped file I/O and the I/O-priority hint are not reliable ("General failure",
    // runtime handle errors seen with 1.15.5.909), so Wine always uses plain synchronous I/O. Windows behaviour is unchanged.
    private static readonly bool s_isWine = DetectWine();

    /// <summary>True when running under Wine (macOS/Linux). Used to pick plain synchronous file I/O.</summary>
    public static bool IsWine => s_isWine;

    private static bool DetectWine()
    {
        try { return Dalamud.Utility.Util.IsWine(); }
        catch { return false; }
    }

    public static FileStream Create(string path)
    {
        return Open(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.SequentialScan);
    }

    public static FileStream OpenRead(string path)
    {
        return Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.SequentialScan);
    }

    public static byte[] ReadAllBytes(string path)
    {
        using var stream = Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.SequentialScan);
        var size = checked((int)stream.Length);
        var result = new byte[size];
        var offset = 0;
        while (offset < size)
        {
            var read = stream.Read(result, offset, Math.Min(ChunkSize, size - offset));
            if (read == 0)
                break;
            offset += read;
        }

        if (offset == size)
            return result;

        Array.Resize(ref result, offset);
        return result;
    }

    public static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        if (s_isWine)
        {
            // Plain synchronous read on a thread pool thread: no FileOptions.Asynchronous, no overlapped I/O, no priority hint.
            return Task.Run(() => ReadAllBytesPlain(path), cancellationToken);
        }

        return ReadAllBytesAsyncCore(path, cancellationToken);
    }

    private static byte[] ReadAllBytesPlain(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.None);
        var size = checked((int)stream.Length);
        var result = new byte[size];
        var offset = 0;
        while (offset < size)
        {
            var read = stream.Read(result, offset, size - offset);
            if (read == 0)
                break;
            offset += read;
        }

        if (offset != size)
            Array.Resize(ref result, offset);
        return result;
    }

    private static async Task<byte[]> ReadAllBytesAsyncCore(string path, CancellationToken cancellationToken)
    {
        await using var stream = Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = checked((int)stream.Length);
        var result = new byte[size];
        var offset = 0;
        while (offset < size)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset, Math.Min(ChunkSize, size - offset)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            offset += read;
        }

        if (offset == size)
            return result;

        Array.Resize(ref result, offset);
        return result;
    }

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        if (!s_isWine)
        {
            WriteChunked(path, data);
            return;
        }

        try
        {
            WriteChunked(path, data);
        }
        catch (IOException)
        {
            // Wine "General failure" on the chunked stream: one retry with the plain framework call.
            File.WriteAllBytes(path, data.ToArray());
        }
    }

    private static void WriteChunked(string path, ReadOnlySpan<byte> data)
    {
        using var stream = Create(path);
        WriteChunks(stream, data);
    }

    private static void WriteChunks(FileStream stream, ReadOnlySpan<byte> data)
    {
        var chunk = s_isWine ? WineWriteChunkSize : ChunkSize;
        while (data.Length > 0)
        {
            var count = Math.Min(chunk, data.Length);
            stream.Write(data[..count]);
            data = data[count..];
        }
    }

    /// <summary>
    /// 1225: writes the file and stamps its creation, last-access and last-write times on the same open handle
    /// (one SetFileTime call), instead of three FileInfo setters that each open the file again afterwards.
    /// Falls back to the path-based setters if the handle call fails.
    /// </summary>
    public static void WriteAllBytesWithTimes(string path, ReadOnlySpan<byte> data, DateTime creationUtc, DateTime lastAccessUtc, DateTime lastWriteUtc)
    {
        bool stamped = false;
        try
        {
            using var stream = Create(path);
            WriteChunks(stream, data);
            stream.Flush();
            stamped = TrySetTimes(stream.SafeFileHandle, creationUtc, lastAccessUtc, lastWriteUtc);
        }
        catch (IOException) when (s_isWine)
        {
            // Wine "General failure" on the chunked stream: one retry with the plain framework call.
            File.WriteAllBytes(path, data.ToArray());
        }

        if (!stamped)
        {
            File.SetCreationTimeUtc(path, creationUtc);
            File.SetLastAccessTimeUtc(path, lastAccessUtc);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }
    }

    private static bool TrySetTimes(SafeFileHandle handle, DateTime creationUtc, DateTime lastAccessUtc, DateTime lastWriteUtc)
    {
        if (handle.IsInvalid) return false;
        try
        {
            long c = creationUtc.ToFileTimeUtc();
            long a = lastAccessUtc.ToFileTimeUtc();
            long w = lastWriteUtc.ToFileTimeUtc();
            if (SetFileTime(handle, ref c, ref a, ref w)) return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // fall through to the managed handle API
        }

        try
        {
            File.SetCreationTimeUtc(handle, creationUtc);
            File.SetLastAccessTimeUtc(handle, lastAccessUtc);
            File.SetLastWriteTimeUtc(handle, lastWriteUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public static void TrySetVeryLow(SafeFileHandle handle)
    {
        if (handle.IsInvalid)
            return;

        var info = new FileIoPriorityHintInfo { PriorityHint = IoPriorityHintVeryLow };
        _ = SetFileInformationByHandle(handle, FileIoPriorityHintInfoClass, ref info, Marshal.SizeOf<FileIoPriorityHintInfo>());
    }

    private static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options)
    {
        if (s_isWine)
        {
            // Never request overlapped I/O or the low-priority hint under Wine.
            return new FileStream(path, mode, access, share, ChunkSize, options & ~FileOptions.Asynchronous);
        }

        var stream = new FileStream(path, mode, access, share, ChunkSize, options);
        TrySetVeryLow(stream.SafeFileHandle);
        return stream;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIoPriorityHintInfo
    {
        public int PriorityHint;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileTime(SafeFileHandle hFile, ref long lpCreationTime, ref long lpLastAccessTime, ref long lpLastWriteTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, ref FileIoPriorityHintInfo lpFileInformation, int dwBufferSize);
}
