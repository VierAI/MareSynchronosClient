namespace MareSynchronos.Services;

/// <summary>
/// 1.15.5.1703: ring buffers of recent resource loads that Penumbra attributes to a game object, for the
/// "Dump all sync info" file (ghost clapping / VFX-bound sounds / unexpected items in the build list). Fed from Penumbra's
/// GameObjectResourcePathResolved event before PSync's own early returns, so unmodded (raw) loads and loads with
/// no game object are kept too. Only .scd (sounds) and .avfx/.tmb/.pap (what usually plays them) are kept.
/// Called from any thread. Nothing here changes what loads or plays.
/// </summary>
public static class PSyncResourceLoadLog
{
    public const int ScdCapacity = 2000;
    public const int OtherCapacity = 1500;

    public readonly record struct Entry(long UtcTicks, nint GameObject, string GamePath, string ResolvedPath, bool OnFramework);

    private static readonly Lock Lock = new();
    private static readonly Entry[] Scd = new Entry[ScdCapacity];
    private static readonly Entry[] Other = new Entry[OtherCapacity];
    private static int _scdNext;
    private static int _scdCount;
    private static int _otherNext;
    private static int _otherCount;
    private static long _scdTotal;
    private static long _otherTotal;

    public static long ScdTotal => Interlocked.Read(ref _scdTotal);
    public static long OtherTotal => Interlocked.Read(ref _otherTotal);

    public static void Record(nint gameObject, string? gamePath, string? resolvedPath, bool onFramework)
    {
        if (gamePath == null || gamePath.Length < 5) return;
        bool isScd = gamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase);
        if (!isScd && !gamePath.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)
            && !gamePath.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)
            && !gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var entry = new Entry(DateTime.UtcNow.Ticks, gameObject, gamePath, resolvedPath ?? string.Empty, onFramework);
        lock (Lock)
        {
            if (isScd)
            {
                Scd[_scdNext] = entry;
                _scdNext = (_scdNext + 1) % ScdCapacity;
                if (_scdCount < ScdCapacity) _scdCount++;
                _scdTotal++;
            }
            else
            {
                Other[_otherNext] = entry;
                _otherNext = (_otherNext + 1) % OtherCapacity;
                if (_otherCount < OtherCapacity) _otherCount++;
                _otherTotal++;
            }
        }
    }

    /// <summary>Oldest first.</summary>
    public static Entry[] Snapshot(bool scd)
    {
        lock (Lock)
        {
            var buf = scd ? Scd : Other;
            var count = scd ? _scdCount : _otherCount;
            var next = scd ? _scdNext : _otherNext;
            var cap = buf.Length;
            var result = new Entry[count];
            var start = (next - count + cap) % cap;
            for (int i = 0; i < count; i++) result[i] = buf[(start + i) % cap];
            return result;
        }
    }
}
