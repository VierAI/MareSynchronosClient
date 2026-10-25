using MareSynchronos.API.Data;
using MareSynchronos.MareConfiguration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MareSynchronos.Services.TextureCap;

/// <summary>
/// Fork (texture size cap). Hooks:
///  - <see cref="ApplyCaps"/> from the pair apply: swaps texture paths to shrunk copies that already exist and queues
///    the pair's file set for background work. Never blocks; the first apply uses full-size textures.
///  - <see cref="AdjustVramSize"/> from the VRAM estimate, so it counts shrunk sizes.
/// Work (classify by model size, mip-trim) runs on 1-2 dedicated BelowNormal threads, one pending job per pair.
/// </summary>
public sealed class TextureCapService : IHostedService, IDisposable
{
    public static TextureCapService? Instance { get; private set; }

    public sealed class PairStats
    {
        public string Name = string.Empty;
        public int Classified;
        public int Capped;
        public int Swapped;
        public long OriginalBytes;
        public long ShrunkBytes;
        public long SavedBytes => OriginalBytes - ShrunkBytes;
        public DateTime LastRun;
    }

    private sealed record Job(string Uid, Dictionary<string, string> Map);

    private readonly ILogger<TextureCapService> _logger;
    private readonly PlayerPerformanceConfigService _perfConfig;
    private readonly TextureSizeClassifier _classifier = new();
    private readonly BlockingCollection<Job> _queue = new(new ConcurrentQueue<Job>(), 512);
    private readonly ConcurrentDictionary<string, Job> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<string, int>> _decisions = new(StringComparer.Ordinal); // uid -> hash -> cap
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _swapped = new(StringComparer.Ordinal); // uid -> hash -> shrunk size
    private readonly ConcurrentDictionary<string, PairStats> _stats = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = new();

    public DownscaledTextureCache Cache { get; }

    public TextureCapService(ILogger<TextureCapService> logger, MareConfigService config, PlayerPerformanceConfigService perfConfig)
    {
        _logger = logger;
        _perfConfig = perfConfig;
        Cache = new DownscaledTextureCache(config);
    }

    private static VierAIConfig Cfg => VierAIConfigStore.Current;

    public int QueueLength => _pending.Count;
    public IReadOnlyDictionary<string, PairStats> Stats => _stats;
    public long TotalSavedBytes => _stats.Values.Sum(s => s.SavedBytes);
    public PairStats? GetStats(string uid) => _stats.TryGetValue(uid, out var s) ? s : null;

    public bool IsExempt(UserData user)
    {
        bool Match(IEnumerable<string> list) => list.Any(u => string.Equals(u, user.UID, StringComparison.Ordinal)
            || (!string.IsNullOrEmpty(user.Alias) && string.Equals(u, user.Alias, StringComparison.Ordinal)));
        return Match(_perfConfig.Current.UIDsToOverride) || Match(Cfg.TextureCapExemptUIDs);
    }

    public Dictionary<string, string> ApplyCaps(UserData user, Dictionary<string, string> moddedPaths)
    {
        try
        {
            if (!Cfg.TextureCapEnabled || IsExempt(user))
            {
                _swapped.TryRemove(user.UID, out _);
                _stats.TryRemove(user.UID, out _);
                return moddedPaths;
            }

            Enqueue(user.UID, moddedPaths);

            if (!_decisions.TryGetValue(user.UID, out var decisions) || decisions.Count == 0)
                return moddedPaths;

            var result = new Dictionary<string, string>(moddedPaths, StringComparer.Ordinal);
            var swapped = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long original = 0, shrunk = 0;
            foreach (var (gamePath, file) in moddedPaths)
            {
                if (!gamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) continue;
                var hash = TextureSizeClassifier.HashOf(file);
                if (hash == null || !decisions.TryGetValue(hash, out var cap) || cap <= 0) continue;
                if (!Cache.TryGet(hash, cap, out var smallPath, out var smallSize)) continue;
                result[gamePath] = smallPath;
                if (swapped.TryAdd(hash, smallSize))
                {
                    try { original += new FileInfo(file).Length; } catch { original += smallSize; }
                    shrunk += smallSize;
                }
            }

            _swapped[user.UID] = swapped;
            var stats = _stats.GetOrAdd(user.UID, _ => new PairStats());
            stats.Name = user.AliasOrUID;
            stats.Swapped = swapped.Count;
            stats.OriginalBytes = original;
            stats.ShrunkBytes = shrunk;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Texture cap: apply hook failed for {uid}, using full-size textures", user.AliasOrUID);
            return moddedPaths;
        }
    }

    /// <summary>VRAM estimate: the shrunk size when this pair is using a shrunk copy of the texture.</summary>
    public long AdjustVramSize(UserData user, string hash, long size)
    {
        if (!Cfg.TextureCapEnabled) return size;
        return _swapped.TryGetValue(user.UID, out var map) && map.TryGetValue(hash, out var small) && small > 0 && small < size ? small : size;
    }

    private void Enqueue(string uid, Dictionary<string, string> map)
    {
        if (!map.Keys.Any(k => k.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))) return;
        var job = new Job(uid, new Dictionary<string, string>(map, StringComparer.Ordinal));
        if (_pending.TryAdd(uid, job))
        {
            if (!_queue.TryAdd(job)) _pending.TryRemove(uid, out _);
        }
        else
        {
            _pending[uid] = job; // newer file set replaces the waiting one
        }
    }

    public void ClearAll()
    {
        _decisions.Clear();
        _swapped.Clear();
        _stats.Clear();
        Cache.Clear();
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var queued in _queue.GetConsumingEnumerable(_cts.Token))
            {
                if (!_pending.TryRemove(queued.Uid, out var job)) job = queued;
                if (!Cfg.TextureCapEnabled) continue;
                try
                {
                    Process(job, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Texture cap: background job failed for {uid}", job.Uid);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private void Process(Job job, CancellationToken ct)
    {
        var decisions = _classifier.Classify(job.Map, Cfg, ct);
        var caps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in decisions) caps[d.Hash] = d.Cap;
        _decisions[job.Uid] = caps;

        int made = 0, capped = 0;
        foreach (var d in decisions.Where(d => d.Cap > 0).DistinctBy(d => d.Hash, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            capped++;
            if (Cache.TryGet(d.Hash, d.Cap, out _, out _) || Cache.HasFailed(d.Hash, d.Cap)) continue;
            if (!File.Exists(d.FilePath)) continue;
            var dest = Cache.PathFor(d.Hash, d.Cap);
            if (TexMipTrimmer.TryTrim(d.FilePath, d.Cap, dest, out var before, out var w, out var h, out var reason))
            {
                Cache.Record(d.Hash, d.Cap, new FileInfo(dest).Length);
                made++;
                _logger.LogDebug("Texture cap: {hash} {ow}x{oh} -> {w}x{h} (model {m:0.000} m, eye {eye})", d.Hash, before.Width, before.Height, w, h, d.LargestModelMetres, d.Eye);
            }
            else
            {
                Cache.MarkFailed(d.Hash, d.Cap, reason);
                _logger.LogDebug("Texture cap: skipped {hash}: {reason}", d.Hash, reason);
            }
        }

        var stats = _stats.GetOrAdd(job.Uid, _ => new PairStats());
        stats.Classified = decisions.Count;
        stats.Capped = capped;
        stats.LastRun = DateTime.Now;
        if (made > 0)
            _logger.LogInformation("Texture cap: made {n} smaller texture(s) for {uid}; used from the next apply", made, job.Uid);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Instance = this;
        var count = Math.Clamp(Cfg.TextureCapWorkerThreads, 1, 2);
        for (var i = 0; i < count; i++)
        {
            var t = new Thread(WorkerLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"PSync texture cap {i}" };
            _threads.Add(t);
            t.Start();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Instance == this) Instance = null;
        _cts.Cancel();
        _queue.CompleteAdding();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Instance == this) Instance = null;
        try { _cts.Cancel(); } catch { /* disposed */ }
        foreach (var t in _threads) t.Join(1000);
        _cts.Dispose();
        _queue.Dispose();
    }
}
