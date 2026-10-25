using System.Collections.Concurrent;

namespace MareSynchronos.Services;

/// <summary>
/// Fork (1.15.5.1205): tells a sustained sound (looping, or long like a song) from a one-shot emote sound by reading the
/// SCD header of the loaded file. Layout checked against ~1600 real .scd files from the sync cache:
/// - 0x00 "SEDBSSCF"; offsets table at 0x30: u16 count at +0x02 = audio entries, u32 at +0x0C = offset of the audio entry offset list.
/// - Audio entry (0x20 bytes): u32 data length, u32 channels, u32 sample rate, u32 format (6 = Vorbis, 12 = MS-ADPCM,
///   0xFFFFFFFF = empty), u32 loop start, u32 loop end.
/// Sustained = any entry with a loop end, or an estimated length of at least <see cref="SustainedSeconds"/>.
/// About 7% of the cached files have loop points; DJ songs usually do not loop but are long, hence the length rule.
/// </summary>
public static class ScdInfo
{
    public const double SustainedSeconds = 20;
    private const int HeaderBytes = 16384;
    private static readonly ConcurrentDictionary<string, bool> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cached answer, or null if the file was not read yet.</summary>
    public static bool? TryGetCached(string filePath) => s_cache.TryGetValue(filePath, out var v) ? v : null;

    /// <summary>Reads the header (off the game thread) and caches the answer. Unreadable files count as one-shot.</summary>
    public static Task<bool> ClassifyAsync(string filePath) => Task.Run(() =>
    {
        if (s_cache.TryGetValue(filePath, out var cached)) return cached;
        var sustained = false;
        try { sustained = IsSustained(filePath); } catch { sustained = false; }
        s_cache[filePath] = sustained;
        return sustained;
    });

    private static bool IsSustained(string filePath)
    {
        byte[] b;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
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
        if (b.Length < 0x40 || !System.Text.Encoding.ASCII.GetString(b, 0, 8).Equals("SEDBSSCF", StringComparison.Ordinal)) return false;

        int count = BitConverter.ToUInt16(b, 0x32);
        var list = (int)BitConverter.ToUInt32(b, 0x3C);
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
            // A loop marker counts as continuous no matter how short: Noises is 'who is making noise right now', and an emote with
            // a short repeating noise is making noise for as long as it loops (VierAI 2026-10-25).
            if (loopEnd > 0) return true;

            double seconds = format switch
            {
                12 when channels > 0 && rate > 0 => length * 2.0 / (channels * rate), // ~4 bits per sample
                _ => length / 24000.0, // Vorbis and others: rough bytes per second
            };
            if (seconds >= SustainedSeconds) return true;
        }
        return false;
    }
}