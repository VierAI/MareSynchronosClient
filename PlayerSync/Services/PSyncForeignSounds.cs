using MareSynchronos.PlayerData.Pairs;
using System.Collections.Concurrent;

namespace MareSynchronos.Services;

/// <summary>
/// 1.15.5.1704 (build list PRIORITY items "VFX-bound sounds" and "Noises window misses a loaded pair's sounds"):
/// sounds a paired player's object loads that are not one of their own synced .scd files.
/// <para>Before 1704 Noises only counted a load if its game path was one of the pair's synced .scd files, and raw (not
/// redirected) loads never reached the pair at all. A synced .tmb/.pap/.avfx that plays a sound under a path the pair did
/// not sync (a vanilla sound, or a .scd under vfx/) was therefore never listed and never muted: "sounds disabled" strips
/// only the .scd entries and leaves the .tmb/.pap/.avfx that reference them.</para>
/// <para>Now a non-own .scd load on the pair's object counts when (a) its game path is referenced inside one of the
/// pair's applied .tmb/.pap/.avfx files (found by a background byte scan), or (b) it is redirected to a file and its game
/// path is under vfx/. Everything else (footsteps, weapon and ability sounds, sounds resolved from other mods) is still
/// ignored, so vanilla noise does not flood the list.</para>
/// <para>Mute: while the pair has sounds disabled, every such path is redirected in that pair's own temporary Penumbra
/// collection to <see cref="MuteTarget"/>, a game path that does not exist, so the load fails and nothing plays. This is the
/// same failed load the existing mute already causes for a stripped custom .scd. Only that pair's collection changes:
/// other players and the local player are not affected.</para>
/// </summary>
public static class PSyncForeignSounds
{
    /// <summary>Game path that does not exist: a load redirected here fails, so nothing plays.</summary>
    public const string MuteTarget = "sound/psync/muted_by_playersync.scd";

    public const int MaxObservedPerPair = 256;
    private const long MaxScanFileBytes = 32L * 1024 * 1024;
    private const int MaxScanCacheEntries = 10000;
    private static readonly ConcurrentDictionary<string, string[]> s_scanCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True if a non-own .scd load on the pair's object should count as the pair's noise (and be muted with them).</summary>
    public static bool Counts(Pair pair, string gamePath, string filePath, bool raw)
    {
        if (string.IsNullOrEmpty(gamePath) || !gamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsMuteTarget(filePath)) return false;
        var refs = pair.PSyncReferencedScdPaths;
        if (refs != null && refs.Contains(gamePath)) return true;
        return !raw && gamePath.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMuteTarget(string? filePath)
        => !string.IsNullOrEmpty(filePath) && string.Equals(ScdInfo.NormalizePath(filePath).Replace('\\', '/'), MuteTarget, StringComparison.OrdinalIgnoreCase);

    /// <summary>True if the resolved path is a file on disk (not a game path).</summary>
    public static bool IsDiskPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var p = ScdInfo.NormalizePath(filePath);
        return p.Length > 2 && (p[1] == ':' || p.StartsWith("\\\\", StringComparison.Ordinal) || p.StartsWith('/'));
    }

    /// <summary>.scd game paths referenced inside these files (applied .tmb/.pap/.avfx cache files). Runs off the game thread;
    /// results are cached per file path. Unreadable or very large files are skipped.</summary>
    public static Task<HashSet<string>> ScanReferencedScdAsync(IReadOnlyCollection<string> files) => Task.Run(() =>
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!s_scanCache.TryGetValue(file, out var paths))
            {
                paths = ScanFile(file);
                if (s_scanCache.Count >= MaxScanCacheEntries) s_scanCache.Clear();
                s_scanCache[file] = paths;
            }
            foreach (var p in paths) result.Add(p);
        }
        return result;
    });

    private static string[] ScanFile(string file)
    {
        try
        {
            var path = ScdInfo.NormalizePath(file);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxScanFileBytes) return [];
            var bytes = File.ReadAllBytes(path);
            return FindScdPaths(bytes);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>ASCII strings like "sound/foo/bar.scd" or "vfx/x/y.scd" inside a binary file.</summary>
    public static string[] FindScdPaths(ReadOnlySpan<byte> bytes)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 4; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] != (byte)'.' || (bytes[i + 1] | 0x20) != 's' || (bytes[i + 2] | 0x20) != 'c' || (bytes[i + 3] | 0x20) != 'd') continue;
            if (i + 4 < bytes.Length && IsPathByte(bytes[i + 4])) continue; // ".scdx..." is not an .scd path
            int start = i;
            while (start > 0 && i - start < 260 && IsPathByte(bytes[start - 1])) start--;
            if (i - start < 3) continue;
            var s = System.Text.Encoding.ASCII.GetString(bytes[start..(i + 4)]).Replace('\\', '/').TrimStart('/');
            if (s.Contains('/', StringComparison.Ordinal) && !s.Contains("//", StringComparison.Ordinal)) found.Add(s.ToLowerInvariant());
        }
        return [.. found];
    }

    private static bool IsPathByte(byte b)
        => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
            or (byte)'_' or (byte)'-' or (byte)'/' or (byte)'\\' or (byte)'.';
}
