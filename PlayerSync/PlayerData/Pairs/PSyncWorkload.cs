namespace MareSynchronos.PlayerData.Pairs;

/// <summary>
/// Fork (1.15.5.1424): one place that sums the synced-player workload, so the debug IPC (PSync.GetPerfSwitchState /
/// GetPairStates v6) and the Help > Their Pair Data > Workload tab always agree. Only reads values PSync already caches
/// per apply (LastAppliedDataBytes, LastAppliedApproximateVRAMBytes, LastAppliedDataTris); -1 means not computed yet
/// and is skipped and counted. No file or model parsing happens here.
/// </summary>
public static class PSyncWorkload
{
    public sealed class Totals
    {
        public int Visible { get; set; }
        public int Loaded { get; set; }
        public int Waiting { get; set; }
        public int Applying { get; set; }
        public int Paused { get; set; }
        public long Bytes { get; set; }
        public long VramBytes { get; set; }
        public long Tris { get; set; }
        public long MaxPairVramBytes { get; set; }
        public long MaxPairTris { get; set; }
        public long MaxPairBytes { get; set; }
        public int BytesNotComputed { get; set; }
        public int VramNotComputed { get; set; }
        public int TrisNotComputed { get; set; }
    }

    /// <summary>Same state rule as the debug IPC rows: applying, then loaded (not paused, has data, applied once), else waiting.</summary>
    public static bool IsLoaded(Pair pair)
        => !pair.HasDataApplicationTaskRunning && !pair.IsPaused && pair.HasCachedData && pair.LastDataApplicationTime > DateTimeOffset.MinValue;

    public static Totals Compute(IEnumerable<Pair> visiblePairs)
    {
        var t = new Totals();
        foreach (var pair in visiblePairs)
        {
            t.Visible++;
            if (pair.HasDataApplicationTaskRunning) { t.Applying++; continue; }
            if (pair.IsPaused) { t.Paused++; continue; }
            if (!IsLoaded(pair)) { t.Waiting++; continue; }
            t.Loaded++;
            Add(pair.LastAppliedDataBytes, v => t.Bytes += v, () => t.BytesNotComputed++, v => { if (v > t.MaxPairBytes) t.MaxPairBytes = v; });
            Add(pair.LastAppliedApproximateVRAMBytes, v => t.VramBytes += v, () => t.VramNotComputed++, v => { if (v > t.MaxPairVramBytes) t.MaxPairVramBytes = v; });
            Add(pair.LastAppliedDataTris, v => t.Tris += v, () => t.TrisNotComputed++, v => { if (v > t.MaxPairTris) t.MaxPairTris = v; });
        }
        return t;
    }

    private static void Add(long value, Action<long> sum, Action missing, Action<long> max)
    {
        if (value < 0) { missing(); return; }
        sum(value);
        max(value);
    }
}
