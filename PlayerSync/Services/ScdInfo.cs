using System.Collections.Concurrent;

namespace MareSynchronos.Services;

/// <summary>What the SCD header says about a loaded sound file (fork 1.15.5.1210).</summary>
/// <param name="Valid">False when the file is missing, unreadable or has no playable audio entry: the load failed.</param>
/// <param name="Looped">Any audio entry has loop points.</param>
/// <param name="Seconds">Estimated length of the longest audio entry.</param>
public readonly record struct ScdClass(bool Valid, bool Looped, double Seconds)
{
    /// <summary>Long enough to count as a song (DJ music etc.).</summary>
    public bool Long => Seconds >= ScdInfo.SustainedSeconds;
    /// <summary>Looping or long: lasts while the loop/performance runs.</summary>
    public bool Sustained => Looped || Long;
    public static readonly ScdClass Failed = new(false, false, 0);
}

/// <summary>
/// Fork (1.15.5.1205, extended 1210): reads the SCD header of a loaded sound file to tell a failed load, a short one-shot,
/// a looping sound and a long song apart. Layout checked against ~1600 real .scd files from the sync cache:
/// - 0x00 "SEDBSSCF"; offsets table at 0x30: u16 count at +0x02 = audio entries, u32 at +0x0C = offset of the audio entry offset list.
/// - Audio entry (0x20 bytes): u32 data length, u32 channels, u32 sample rate, u32 format (6 = Vorbis, 12 = MS-ADPCM,
///   0xFFFFFFFF = empty), u32 loop start, u32 loop end.
/// </summary>
public static class ScdInfo
{
    public const double SustainedSeconds = 20;
    private const int HeaderBytes = 16384;
    private const int MaxCacheEntries = 20000;
    private static readonly ConcurrentDictionary<string, ScdClass> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cached answer, or null if the file was not read yet.</summary>
    public static ScdClass? TryGetInfo(string filePath) => s_cache.TryGetValue(filePath, out var v) ? v : null;

    /// <summary>Reads the header (off the game thread) and caches the answer. Missing or unreadable files count as failed.
    /// Failed answers are not cached, so a file that finishes downloading later is read again.</summary>
    public static Task<ScdClass> ClassifyInfoAsync(string filePath) => Task.Run(() =>
    {
        if (s_cache.TryGetValue(filePath, out var cached)) return cached;
        // Club fix: a busy player loads many sounds while the cache file can still be held by the compactor or the
        // game, so an IO error is retried a few times instead of marking the load failed. Only a file that is really
        // missing or is not an SCD counts as failed.
        var path = NormalizePath(filePath);
        ScdClass info = ScdClass.Failed;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { info = Read(path); break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(150 * (attempt + 1)); }
            catch { info = ScdClass.Failed; break; }
        }
        if (info.Valid)
        {
            // 1225 (audit 8): bounded; a cleared entry is just read again (16 KB header) the next time it is needed.
            if (s_cache.Count >= MaxCacheEntries) s_cache.Clear();
            s_cache[filePath] = info;
        }
        return info;
    });

    /// <summary>Penumbra can report a path with a "|prefix|" in front; strip it so the real file is read.</summary>
    public static string NormalizePath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || filePath[0] != '|') return filePath;
        var end = filePath.IndexOf('|', 1);
        return end > 0 ? filePath[(end + 1)..] : filePath;
    }

    private static ScdClass Read(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return ScdClass.Failed;
        byte[] b;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            b = new byte[Math.Min(HeaderBytes, fs.Length)];
            var read = 0;
            while (read < b.Length)
            {
                var n = fs.Read(b, read, b.Length - read);
                if (n <= 0) break;
                read += n;
            }
        }
        if (b.Length < 0x40 || !System.Text.Encoding.ASCII.GetString(b, 0, 8).Equals("SEDBSSCF", StringComparison.Ordinal)) return ScdClass.Failed;

        int count = BitConverter.ToUInt16(b, 0x32);
        var list = (int)BitConverter.ToUInt32(b, 0x3C);
        var any = false;
        var looped = false;
        double longest = 0;
        for (var i = 0; i < count && list + (4 * i) + 4 <= b.Length; i++)
        {
            var e = (int)BitConverter.ToUInt32(b, list + (4 * i));
            if (e <= 0 || e + 0x20 > b.Length) continue;
            var length = BitConverter.ToUInt32(b, e);
            var channels = BitConverter.ToUInt32(b, e + 4);
            var rate = BitConverter.ToUInt32(b, e + 8);
            var format = BitConverter.ToUInt32(b, e + 12);
            var loopEnd = BitConverter.ToUInt32(b, e + 0x14);
            if (format == uint.MaxValue || length == 0) continue;
            any = true;
            // A loop marker counts as continuous no matter how short (VierAI 2026-10-25).
            if (loopEnd > 0) looped = true;
            double seconds = format switch
            {
                12 when channels > 0 && rate > 0 => length * 2.0 / (channels * rate), // ~4 bits per sample
                _ => length / 24000.0, // Vorbis and others: rough bytes per second
            };
            longest = Math.Max(longest, seconds);
        }
        // Header without any audio entry (e.g. a file that is only a placeholder) is treated like an ordinary one-shot.
        return any ? new ScdClass(true, looped, longest) : new ScdClass(true, false, 0);
    }
}
