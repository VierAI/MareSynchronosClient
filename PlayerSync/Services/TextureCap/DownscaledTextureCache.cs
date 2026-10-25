using MareSynchronos.MareConfiguration;
using System.Collections.Concurrent;

namespace MareSynchronos.Services.TextureCap;

/// <summary>
/// Fork (texture size cap): shrunk texture copies live in &lt;cache&gt;/downscaled/&lt;HASH&gt;_&lt;cap&gt;.tex. The key is the hash of
/// the file actually applied (after any compressed-alternate swap). Originals in the cache are never touched; the main
/// cache scanners only look at the top level of the cache folder, so this subfolder is invisible to them.
/// </summary>
public sealed class DownscaledTextureCache
{
    public const string FolderName = "downscaled";

    private readonly MareConfigService _config;
    private readonly ConcurrentDictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase); // "HASH_cap" -> bytes, -1 = missing
    private readonly ConcurrentDictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase); // "HASH_cap" -> reason

    public DownscaledTextureCache(MareConfigService config) => _config = config;

    public string Folder => Path.Combine(_config.Current.CacheFolder, FolderName);

    public static string Key(string hash, int cap) => $"{hash.ToUpperInvariant()}_{cap}";

    public string PathFor(string hash, int cap) => Path.Combine(Folder, Key(hash, cap) + ".tex");

    /// <summary>Path and size of an existing shrunk copy, if there is one.</summary>
    public bool TryGet(string hash, int cap, out string path, out long size)
    {
        path = PathFor(hash, cap);
        var key = Key(hash, cap);
        if (!_sizes.TryGetValue(key, out size))
        {
            try
            {
                var fi = new FileInfo(path);
                size = fi.Exists ? fi.Length : -1;
            }
            catch
            {
                size = -1;
            }
            // Only remember hits; a miss may become a hit once the worker finishes.
            if (size > 0) _sizes[key] = size;
        }
        return size > 0;
    }

    public void Record(string hash, int cap, long size) => _sizes[Key(hash, cap)] = size;

    public void MarkFailed(string hash, int cap, string reason) => _failed[Key(hash, cap)] = reason;

    public bool HasFailed(string hash, int cap) => _failed.ContainsKey(Key(hash, cap));

    public int FailedCount => _failed.Count;

    public (int Files, long Bytes) DiskUsage()
    {
        try
        {
            if (!Directory.Exists(Folder)) return (0, 0);
            var files = new DirectoryInfo(Folder).GetFiles("*.tex");
            return (files.Length, files.Sum(f => f.Length));
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>Deletes all shrunk copies (originals are untouched). Pairs pick up full-size textures on their next apply.</summary>
    public void Clear()
    {
        _sizes.Clear();
        _failed.Clear();
        try
        {
            if (Directory.Exists(Folder))
                foreach (var f in Directory.EnumerateFiles(Folder))
                    try { File.Delete(f); } catch { /* in use; next clear */ }
        }
        catch
        {
            // ignore
        }
    }
}
