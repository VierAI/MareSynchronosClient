using MareSynchronos.FileCache;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MareSynchronos.Utils;

public static class Crypto
{
    private static readonly ConcurrentDictionary<(string, ushort), string> _hashListPlayersSHA256 = new();
    private static readonly ConcurrentDictionary<string, string> _hashListSHA256 = new(StringComparer.Ordinal);

    public static string GetFileHash(this string filePath)
    {
        // Streaming hash to avoid allocating an entire file buffer
        using var sha1 = SHA1.Create();
        try
        {
            using var fs = LowPriorityFile.OpenRead(filePath);
            return Convert.ToHexString(sha1.ComputeHash(fs));
        }
        catch (IOException) when (LowPriorityFile.IsWine)
        {
            // Wine "General failure": one retry with a plain stream (Windows behaviour unchanged: the exception propagates).
            using var plain = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.None);
            return Convert.ToHexString(sha1.ComputeHash(plain));
        }
    }

    public static string GetBytesHash(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexString(SHA1.HashData(data));
    }

    public static async Task<string> GetStreamHashAsync(Stream stream, CancellationToken token = default)
    {
        return Convert.ToHexString(await SHA1.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    public static string GetHash256(this (string, ushort) playerToHash)
    {
        return _hashListPlayersSHA256.GetOrAdd(playerToHash, static key => HashString(key.Item1 + key.Item2.ToString()));
    }

    public static string GetHash256(this string stringToHash)
    {
        return _hashListSHA256.GetOrAdd(stringToHash, static value => HashString(value));
    }

    // SHA256.HashData does not share a hasher. The hex form matches the old BitConverter string.
    private static string HashString(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToString(hash).Replace("-", "", StringComparison.Ordinal);
    }
}
