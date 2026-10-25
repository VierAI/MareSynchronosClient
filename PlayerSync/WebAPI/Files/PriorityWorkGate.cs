using MareSynchronos.PlayerData.Pairs;

namespace MareSynchronos.WebAPI.Files;

/// <summary>
/// 1.15.5.1202, priority (d): a counting gate like SemaphoreSlim, but when it is full the next free slot goes to the best
/// waiting tier (see <see cref="PriorityTiers"/>), ties by arrival. Same strict model as downloads: nothing is reserved,
/// running work is never interrupted, and with no contention it behaves exactly like the old semaphore.
/// Used for the pair data apply gate and the incoming-file validation (hash/read) step.
/// </summary>
public sealed class PriorityWorkGate
{
    /// <summary>Tier lookup, set by <see cref="DownloadPriorityQueue"/> so apply/validation use the same tiers as downloads.</summary>
    public static Func<Pair?, int> TierResolver { get; set; } = _ => PriorityTiers.Everyone;

    public static readonly PriorityWorkGate Apply = new(4);
    public static readonly PriorityWorkGate Validation = new(4);

    private sealed class Waiter
    {
        public required int Tier { get; init; }
        public required long Sequence { get; init; }
        public required TaskCompletionSource Tcs { get; init; }
    }

    private readonly object _lock = new();
    private readonly List<Waiter> _waiters = [];
    private readonly int _capacity;
    private int _inUse;
    private long _sequence;

    public PriorityWorkGate(int capacity) => _capacity = Math.Max(1, capacity);

    public static int SafeTier(Pair? pair)
    {
        try { return TierResolver(pair); }
        catch { return PriorityTiers.Everyone; }
    }

    public Task WaitAsync(Pair? pair, CancellationToken ct) => WaitAsync(SafeTier(pair), ct);

    public async Task WaitAsync(int tier, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_lock)
        {
            if (_inUse < _capacity && _waiters.Count == 0)
            {
                _inUse++;
                return;
            }
            waiter = new Waiter { Tier = tier, Sequence = _sequence++, Tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            _waiters.Add(waiter);
            PumpLocked();
        }

        using var reg = ct.Register(() =>
        {
            bool removed;
            lock (_lock) removed = _waiters.Remove(waiter);
            if (removed) waiter.Tcs.TrySetCanceled(ct);
        });
        await waiter.Tcs.Task.ConfigureAwait(false);
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_inUse > 0) _inUse--;
            PumpLocked();
        }
    }

    private void PumpLocked()
    {
        while (_inUse < _capacity && _waiters.Count > 0)
        {
            var best = 0;
            for (var i = 1; i < _waiters.Count; i++)
            {
                var w = _waiters[i];
                var b = _waiters[best];
                if (w.Tier < b.Tier || (w.Tier == b.Tier && w.Sequence < b.Sequence)) best = i;
            }
            var next = _waiters[best];
            _waiters.RemoveAt(best);
            _inUse++;
            // RunContinuationsAsynchronously: no continuation runs under the lock.
            if (!next.Tcs.TrySetResult()) _inUse--;
        }
    }
}