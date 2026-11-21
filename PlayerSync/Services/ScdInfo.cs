using Dalamud.Plugin.Services;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace MareSynchronos.Services;

/// <summary>What the SCD header says about a loaded sound file (fork 1.15.5.1210).</summary>
/// <param name="Valid">False when the file is missing, unreadable or has no playable audio entry: the load failed.</param>
/// <param name="Looped">Any audio entry has loop points.</param>
/// <param name="Seconds">Estimated length of the longest audio entry.</param>
/// <param name="Channels">1.15.6.202: mixer channel(s) from the sound entries' bus numbers (None = not read / no sound entry).</param>
/// <param name="UnmappedBus">1.15.6.205: first bus number with no known channel (shown as "bus N" instead of "?"), -1 = none.</param>
public readonly record struct ScdClass(bool Valid, bool Looped, double Seconds, SoundChannels Channels = SoundChannels.None, int UnmappedBus = -1)
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
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new ScdReader(fs);
        return Parse(r);
    }

    // ---- 1.15.6.205: game files (not on disk) ----

    private static IDataManager? s_gameData;
    private const string GameKeyPrefix = "game:";

    /// <summary>Set once from the plugin constructor: lets a sound that is not on disk be checked against the game data.</summary>
    public static void AttachGameData(IDataManager? dataManager) => s_gameData = dataManager;

    /// <summary>Cached answer for a game path (not on disk), or null if it was not checked yet.</summary>
    public static ScdClass? TryGetGameInfo(string gamePath) => s_cache.TryGetValue(GameKeyPrefix + gamePath, out var v) ? v : null;

    /// <summary>
    /// 1.15.6.205: a sound request whose file is not on disk. Missing from the game data = the load failed and nothing
    /// played (custom mod paths the player's data does not contain, Penumbra logs "Failed to load resource"): Failed.
    /// Present = a real game file: its header is read from the game data, so the channel comes from its real bus number
    /// instead of a guess from the path. Both answers are cached (game data does not change while the game runs).
    /// Without game data access the old behaviour stays: a short sound, channel by path.
    /// </summary>
    public static Task<ScdClass> ClassifyGameFileAsync(string gamePath) => Task.Run(() =>
    {
        var key = GameKeyPrefix + gamePath;
        if (s_cache.TryGetValue(key, out var cached)) return cached;
        var dm = s_gameData;
        if (dm == null) return new ScdClass(true, false, 0);
        ScdClass info;
        try
        {
            var path = NormalizePath(gamePath).Replace('\\', '/').ToLowerInvariant();
            if (!dm.FileExists(path)) info = ScdClass.Failed;
            else
            {
                var data = dm.GetFile(path)?.Data;
                if (data == null) info = ScdClass.Failed;
                else
                {
                    using var r = new ScdReader(data);
                    info = Parse(r);
                }
            }
        }
        catch
        {
            return new ScdClass(true, false, 0); // could not check: keep the old "short sound, channel by path" answer, uncached
        }
        if (s_cache.Count >= MaxCacheEntries) s_cache.Clear();
        s_cache[key] = info;
        return info;
    });

    // ---- header parsing ----

    /// <summary>Upper bounds against corrupt headers.</summary>
    private const int MaxAudioEntries = 4096;
    private const int MaxSoundEntries = 1024;

    private static ScdClass Parse(ScdReader r)
    {
        if (r.Length < 0x40 || !r.IsMagic()) return ScdClass.Failed;
        if (!r.U16(0x32, out var count) || !r.U32(0x3C, out var listU)) return ScdClass.Failed;
        long list = listU;
        var any = false;
        var looped = false;
        double longest = 0;
        for (var i = 0; i < count && i < MaxAudioEntries; i++)
        {
            if (!r.U32(list + (4L * i), out var eU)) break;
            long e = eU;
            if (e <= 0 || e + 0x20 > r.Length) continue;
            // 1.15.6.205: entries past the first 16 KB are read from the file instead of being skipped.
            if (!r.U32(e, out var length) || !r.U32(e + 4, out var channels) || !r.U32(e + 8, out var rate)
                || !r.U32(e + 12, out var format) || !r.U32(e + 0x14, out var loopEnd)) continue;
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
        var (mixer, unmapped) = ReadChannels(r);
        return any ? new ScdClass(true, looped, longest, mixer, unmapped) : new ScdClass(true, false, 0, mixer, unmapped);
    }

    /// <summary>1.15.6.202: sound entry table (offset list right after the 0x20-byte offsets header): byte 1 of each entry is the bus number.
    /// 1.15.6.205: also returns the first bus number that has no mapped channel (-1 = none), and reads entries past 16 KB.</summary>
    private static (SoundChannels Channels, int UnmappedBus) ReadChannels(ScdReader r)
    {
        try
        {
            int hs = r.I16(0x0E, out var h) ? h : 0;
            if (hs <= 0 || hs + 0x20 > r.Length) hs = 0x30;
            if (!r.I16(hs, out var soundCount)) return (SoundChannels.None, -1);
            long list = hs + 0x20;
            var ch = SoundChannels.None;
            var unmapped = -1;
            for (var i = 0; i < soundCount && i < MaxSoundEntries; i++)
            {
                if (!r.I32(list + (4L * i), out var o)) break;
                if (o <= 0 || o + 2 > r.Length) continue;
                if (!r.U8(o + 1, out var bus)) continue;
                var c = SoundChannelMap.FromBus(bus);
                if (c == SoundChannels.Unknown && unmapped < 0) unmapped = bus;
                ch |= c;
            }
            return (ch, unmapped);
        }
        catch { return (SoundChannels.None, -1); }
    }

    /// <summary>
    /// 1.15.6.205: SCD bytes. The first <see cref="HeaderBytes"/> are read once; anything past them (entries of a large file
    /// whose tables or later audio entries sit beyond 16 KB) is read from the stream on demand. In-memory data (game files)
    /// is used as is.
    /// </summary>
    private sealed class ScdReader : IDisposable
    {
        private readonly byte[] _head;
        private readonly Stream? _stream;
        public long Length { get; }

        public ScdReader(byte[] data) { _head = data; Length = data.Length; }

        public ScdReader(Stream stream)
        {
            _stream = stream;
            Length = stream.Length;
            _head = new byte[(int)Math.Min(HeaderBytes, Length)];
            var read = 0;
            while (read < _head.Length)
            {
                var n = stream.Read(_head, read, _head.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < _head.Length) Array.Resize(ref _head, read);
        }

        public bool IsMagic() => _head.Length >= 8 && System.Text.Encoding.ASCII.GetString(_head, 0, 8).Equals("SEDBSSCF", StringComparison.Ordinal);

        private bool Bytes(long offset, Span<byte> dst)
        {
            if (offset < 0 || offset + dst.Length > Length) return false;
            if (offset + dst.Length <= _head.Length) { _head.AsSpan((int)offset, dst.Length).CopyTo(dst); return true; }
            if (_stream == null) return false;
            _stream.Position = offset;
            var read = 0;
            while (read < dst.Length)
            {
                var n = _stream.Read(dst[read..]);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        public bool U8(long o, out byte v)
        {
            Span<byte> s = stackalloc byte[1];
            v = 0;
            if (!Bytes(o, s)) return false;
            v = s[0];
            return true;
        }
        public bool U16(long o, out ushort v)
        {
            Span<byte> s = stackalloc byte[2];
            v = 0;
            if (!Bytes(o, s)) return false;
            v = BinaryPrimitives.ReadUInt16LittleEndian(s);
            return true;
        }
        public bool I16(long o, out short v)
        {
            Span<byte> s = stackalloc byte[2];
            v = 0;
            if (!Bytes(o, s)) return false;
            v = BinaryPrimitives.ReadInt16LittleEndian(s);
            return true;
        }
        public bool U32(long o, out uint v)
        {
            Span<byte> s = stackalloc byte[4];
            v = 0;
            if (!Bytes(o, s)) return false;
            v = BinaryPrimitives.ReadUInt32LittleEndian(s);
            return true;
        }
        public bool I32(long o, out int v)
        {
            Span<byte> s = stackalloc byte[4];
            v = 0;
            if (!Bytes(o, s)) return false;
            v = BinaryPrimitives.ReadInt32LittleEndian(s);
            return true;
        }

        public void Dispose() { } // the stream belongs to the caller
    }
}
