using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using Microsoft.Extensions.Logging;

namespace MareSynchronos.WebAPI.Files;

/// <summary>
/// Tier-aware queue in front of the download slot semaphore. Every download waits here for a slot.
/// Each download has a tier (see <see cref="PriorityTiers"/>, 0 = best). Strict priority ordering of free slots:
/// whenever a slot is free it goes to the best waiting tier, ties by arrival. Nothing is reserved, paused or slowed:
/// tier 0 can use every slot, tier 1 whatever tier 0 is not using, and so on. Running downloads are never interrupted.
/// Slot accounting stays in <see cref="FileTransferOrchestrator"/>; this class only decides who gets the next free slot.
/// </summary>
public sealed class DownloadPriorityQueue
{
    private sealed class Waiter
    {
        public required string Key { get; init; }
        public int Tier { get; set; }
        public required long Sequence { get; init; }
        public required TaskCompletionSource Tcs { get; init; }
        public long EnqueuedTick { get; } = Environment.TickCount64;
        // Players that need this file; the tier is the best tier among them (re-evaluated by Retier). Guarded by _lock.
        public List<Pair> Pairs { get; } = [];
    }

    private readonly ILogger<DownloadPriorityQueue> _logger;
    private readonly InGameFriendService _friends;
    private readonly object _lock = new();
    private readonly List<Waiter> _waiters = [];
    // Slot holders by key, value = tier of the holder.
    private readonly Dictionary<string, int> _holders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Pair>> _holderPairs = new(StringComparer.Ordinal);
    private Func<bool> _tryAcquireSlot = () => false;
    private long _sequence;
    // Club fix: longest slot wait per player UID since the last completed apply (for the "completed in" line). Guarded by _lock.
    private readonly Dictionary<string, long> _waitMs = new(StringComparer.Ordinal);

    /// <summary>The running queue, for read-only debug readers (the debug IPC and the apply log line).</summary>
    public static DownloadPriorityQueue? Instance { get; private set; }

    public DownloadPriorityQueue(ILogger<DownloadPriorityQueue> logger, InGameFriendService friends)
    {
        _logger = logger;
        _friends = friends;
        PriorityWorkGate.TierResolver = GetTier;
        Instance = this;
    }

    /// <summary>Longest download slot wait (ms) for this player since the last call, then resets it.</summary>
    public long TakeQueueWaitMs(Pair pair)
    {
        lock (_lock)
        {
            return _waitMs.Remove(pair.UserData.UID, out var ms) ? ms : 0;
        }
    }

    /// <summary>Read-only queue view for one player: files waiting for a slot, best position among all waiting files
    /// (1 = next, 0 = nothing waiting) and files holding a slot right now.</summary>
    public (int Waiting, int BestPosition, int Running) GetQueueInfo(Pair pair)
    {
        lock (_lock)
        {
            var ordered = _waiters.OrderBy(w => w.Tier).ThenBy(w => w.Sequence).ToList();
            int waiting = 0, best = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!ordered[i].Pairs.Contains(pair)) continue;
                waiting++;
                if (best == 0) best = i + 1;
            }
            var running = _holderPairs.Values.Count(l => l.Contains(pair));
            return (waiting, best, running);
        }
    }

    /// <summary>1.15.5.1401: totals for the debug IPC: files waiting for a slot and slots held right now.</summary>
    public (int Waiting, int Running) GetTotals()
    {
        lock (_lock)
        {
            return (_waiters.Count, _holders.Count);
        }
    }

    /// <summary>Called once by the orchestrator: tries to take a free slot from the semaphore without waiting.</summary>
    public void SetSlotAcquirer(Func<bool> tryAcquireSlot) => _tryAcquireSlot = tryAcquireSlot;

    /// <summary>The tier of the batch's player.</summary>
    public int GetTier(DownloadBatchInfo batch) => GetTier(batch.Pair);

    public int GetTier(Pair? pair)
    {
        if (pair == null) return PriorityTiers.Everyone;
        var cfg = VierAIConfigStore.Current;
        var uid = pair.UserData.UID;
        var alias = pair.UserData.Alias;
        var listed = cfg.UIDsToPrioritize.Exists(p =>
            string.Equals(p, uid, StringComparison.Ordinal) || (!string.IsNullOrEmpty(alias) && string.Equals(p, alias, StringComparison.Ordinal)));
        return PriorityTiers.Classify(listed, _friends.IsFriend(pair.HashedCID), _friends.IsPartyMember(pair.HashedCID), pair.IsDirectlyPaired && !pair.IsOneSidedPair);
    }

    /// <summary>Starts waiting for a slot. The slot has been taken from the semaphore when the task completes.</summary>
    public Task WaitForSlotAsync(string key, int tier, CancellationToken token, Pair? pair = null)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = new Waiter { Key = key, Tier = tier, Sequence = Interlocked.Increment(ref _sequence), Tcs = tcs };
        if (pair != null) waiter.Pairs.Add(pair);
        lock (_lock)
        {
            _waiters.Add(waiter);
        }

        if (token.CanBeCanceled)
        {
            var reg = token.Register(() =>
            {
                lock (_lock)
                {
                    _waiters.Remove(waiter);
                }
                // Does nothing if a slot was already handed over; the caller then runs and releases as usual.
                tcs.TrySetCanceled(token);
            });
            tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
        }

        Pump();
        return tcs.Task;
    }

    /// <summary>Raises a queued or running download to a better tier (a higher-tier player now needs a file that is already in flight).</summary>
    public void Promote(string key, int tier, Pair? pair = null)
    {
        var from = -1;
        lock (_lock)
        {
            var waiter = _waiters.Find(w => string.Equals(w.Key, key, StringComparison.Ordinal));
            if (waiter != null)
            {
                if (pair != null && !waiter.Pairs.Contains(pair)) waiter.Pairs.Add(pair);
                if (tier < waiter.Tier) { from = waiter.Tier; waiter.Tier = tier; }
            }
            else if (_holders.TryGetValue(key, out var current))
            {
                if (pair != null && _holderPairs.TryGetValue(key, out var holderPairs) && !holderPairs.Contains(pair)) holderPairs.Add(pair);
                if (tier < current)
                {
                    from = current;
                    _holders[key] = tier;
                }
            }
        }

        if (from >= 0)
            _logger.LogInformation("Priority: promoted {hash} from tier {from} to tier {to} ({name})", key, from, tier, PriorityTiers.Name(tier));
        Pump();
    }

    /// <summary>Forgets the slot holder. Call before the semaphore slot is released, then call <see cref="Pump"/>.</summary>
    public void OnSlotReleased(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_lock)
        {
            _holders.Remove(key);
            _holderPairs.Remove(key);
        }
    }

    /// <summary>
    /// Re-computes the tier of every waiting and running download from the players that need it, after the priority list
    /// or the in-game friends changed. Running downloads are never paused, only re-tiered. Then hands out free slots again.
    /// </summary>
    public void Retier(string reason)
    {
        var changed = 0;
        var considered = 0;
        lock (_lock)
        {
            try
            {
                foreach (var w in _waiters)
                {
                    if (w.Pairs.Count == 0) continue;
                    considered++;
                    var t = BestTierOf(w.Pairs);
                    if (t != w.Tier) { w.Tier = t; changed++; }
                }

                foreach (var key in _holders.Keys.ToList())
                {
                    if (!_holderPairs.TryGetValue(key, out var pairs) || pairs.Count == 0) continue;
                    considered++;
                    var t = BestTierOf(pairs);
                    if (t != _holders[key]) { _holders[key] = t; changed++; }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Priority: re-tier failed");
            }
        }

        _logger.Log(changed > 0 ? LogLevel.Information : LogLevel.Debug,
            "Priority: re-evaluated tiers after {reason}: {changed} of {considered} download(s) changed tier", reason, changed, considered);
        Pump();
    }

    private int BestTierOf(List<Pair> pairs)
    {
        var best = PriorityTiers.Everyone;
        foreach (var p in pairs)
        {
            var t = GetTier(p);
            if (t < best) best = t;
        }

        return best;
    }

    /// <summary>Hands out free slots to waiting downloads, best tier first, then by arrival.</summary>
    public void Pump()
    {
        lock (_lock)
        {
            while (_waiters.Count > 0)
            {
                var next = PickNextLocked();
                if (!_tryAcquireSlot()) break;

                _waiters.Remove(next);
                _holders[next.Key] = next.Tier;
                _holderPairs[next.Key] = next.Pairs;
                var waited = Environment.TickCount64 - next.EnqueuedTick;
                foreach (var wp in next.Pairs)
                {
                    var uid = wp.UserData.UID;
                    if (!_waitMs.TryGetValue(uid, out var prev) || waited > prev) _waitMs[uid] = waited;
                }
                next.Tcs.TrySetResult();
            }
        }
    }

    private static int DistanceBucket(Waiter w)
    {
        float best = float.MaxValue;
        foreach (var p in w.Pairs)
        {
            var d = MareSynchronos.Services.PSyncCrowd.DistanceOf(p.UserData.UID);
            if (d < best) best = d;
        }
        return best >= 10_000f ? int.MaxValue : (int)(best / 10f);
    }

    private Waiter PickNextLocked()
    {
        // 1.15.5.1217: tier first, then distance (10-yalm buckets so small movements do not reshuffle), then arrival order.
        var pick = _waiters[0];
        var pickBucket = DistanceBucket(pick);
        foreach (var w in _waiters)
        {
            if (w.Tier > pick.Tier) continue;
            var bucket = DistanceBucket(w);
            if (w.Tier < pick.Tier || bucket < pickBucket || (bucket == pickBucket && w.Sequence < pick.Sequence))
            {
                pick = w;
                pickBucket = bucket;
            }
        }

        return pick;
    }
}