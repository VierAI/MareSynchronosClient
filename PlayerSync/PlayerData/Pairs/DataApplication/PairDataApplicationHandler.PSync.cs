using MareSynchronos.Services.Mediator;
using MareSynchronos.API.Data;
using MareSynchronos.Services;
using MareSynchronos.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MareSynchronos.PlayerData.Pairs;

// 1.15.5.1217: force re-download, missing-file auto retry, budget hold seeding.
public sealed partial class PairDataApplicationHandler
{
    private const int PSyncMaxAutoRetries = 3;
    private static readonly TimeSpan PSyncRetryWindow = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, List<DateTimeOffset>> s_psyncRetries = new(StringComparer.Ordinal);
    private int _psyncRetryScheduled;
    private int _psyncApplyInFlight;

    /// <summary>After a budget/block release: keep the last data as pending so a later reapply can use it.</summary>
    public void PSyncSeedPendingData(CharacterData data)
    {
        lock (_cachedDataLock)
        {
            _pendingData ??= data;
        }
    }

    public CharacterData? PSyncGetAnyData()
    {
        lock (_cachedDataLock)
        {
            return (_cachedData ?? _pendingData)?.DeepClone();
        }
    }

    /// <summary>Removes this pair's files from the PSync download cache (never Penumbra mod files) so the next apply downloads them fresh.</summary>
    public async Task<int> PSyncForceRedownloadAsync(IEnumerable<string>? onlyHashes = null, bool deleteFiles = true)
    {
        var hashes = onlyHashes?.Where(h => !string.IsNullOrWhiteSpace(h)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (hashes == null)
        {
            var data = PSyncGetAnyData();
            if (data == null) return 0;
            hashes = data.FileReplacements.SelectMany(k => k.Value).Select(r => r.Hash)
                .Where(h => !string.IsNullOrWhiteSpace(h)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var redirect in ActiveCompressionRedirects.Values) hashes.Add(redirect);
        }
        if (hashes.Count == 0) return 0;

        // Audit L1: never delete a file another visible pair is using right now; those are kept and simply re-used.
        int shared = 0;
        if (deleteFiles)
        {
            try
            {
                var others = PSyncCrowd.VisiblePairsProvider?.Invoke() ?? new List<Pair>();
                var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var other in others)
                {
                    if (ReferenceEquals(other, Pair) || string.Equals(other.UserData.UID, Pair.UserData.UID, StringComparison.Ordinal)) continue;
                    foreach (var h in other.PSyncGetFileHashes()) inUse.Add(h);
                }
                shared = hashes.RemoveWhere(inUse.Contains);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "PSync: could not check other pairs' files; keeping all shared candidates");
                return 0;
            }
            if (hashes.Count == 0)
            {
                Logger.LogInformation("PSync: all {n} file(s) of {pair} are shared with other visible pairs; nothing removed", shared, Pair.PairUIDName);
                return 0;
            }
        }

        // Audit L1c: cancel any apply in flight for this pair and wait (bounded) for its download task to stop
        // before deleting, so an apply never reads a file we are removing. The follow-up apply re-downloads.
        if (deleteFiles)
        {
            try { _dataApplicationCancellationTokenSource?.Cancel(); }
            catch (ObjectDisposedException) { }
            // L1d: wait (bounded) for the download task AND for any apply that is already past the download step.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            var dl = _pairDownloadTask;
            while (DateTime.UtcNow < deadline && ((dl != null && !dl.IsCompleted) || Volatile.Read(ref _psyncApplyInFlight) > 0))
                await Task.Delay(100).ConfigureAwait(false);
            if ((dl != null && !dl.IsCompleted) || Volatile.Read(ref _psyncApplyInFlight) > 0)
            {
                Logger.LogWarning("PSync: apply for {pair} still running after cancel; force re-download skipped, try again", Pair.PairUIDName);
                Mediator.Publish(new NotificationMessage("Force re-download skipped",
                    $"{Pair.UserData.AliasOrUID} is still loading. Nothing was removed; try Force Re-download again in a moment.",
                    MareSynchronos.MareConfiguration.Models.NotificationType.Warning));
                return 0;
            }
        }

        int removed = 0;
        var entries = await _fileDbManager.GetFirstFileCacheEntitiesByHashes(hashes, CancellationToken.None, validate: false, cacheResults: false).ConfigureAwait(false);
        foreach (var entry in entries.Values)
        {
            if (entry == null || !entry.IsCacheEntry) continue; // only our own cache, never Penumbra files
            try
            {
                if (deleteFiles && File.Exists(entry.ResolvedFilepath)) File.Delete(entry.ResolvedFilepath);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "PSync: could not delete cache file for {hash}", entry.Hash);
            }
            // Audit L1: only forget the DB entry when the file is really gone (deleted now, or already missing).
            bool gone;
            try { gone = !File.Exists(entry.ResolvedFilepath); } catch { gone = false; }
            if (!gone)
            {
                Logger.LogDebug("PSync: cache file for {hash} is still there (in use?); keeping its entry", entry.Hash);
                continue;
            }
            if (await _fileDbManager.RemoveFileCacheEntity(entry).ConfigureAwait(false)) removed++;
        }
        Logger.LogInformation("PSync: removed {count} cache entr(ies) for {pair} for re-download ({shared} shared with other visible pairs kept)", removed, Pair.PairUIDName, shared);
        return removed;
    }

    /// <summary>"Missing files for" path: drop stale cache entries and schedule one capped forced reapply.</summary>
    private void PSyncHandleMissing(string[] hashes, string why)
    {
        try
        {
            if (!VierAIConfigStore.Current.AutoRedownloadMissingFiles || hashes.Length == 0) return;
            if (PSyncCrowd.IsBlocked(Pair)) return;
            lock (_downloadManager.ForbiddenTransfers)
            {
                hashes = hashes.Where(h => !_downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, h, StringComparison.OrdinalIgnoreCase))
                    && !PSyncFileSafety.IsRefusedForPair(Pair.UserData.UID, h)).ToArray();
            }
            if (hashes.Length == 0) return;
            var uid = Pair.UserData.UID;
            var now = DateTimeOffset.UtcNow;
            var list = s_psyncRetries.GetOrAdd(uid, _ => new());
            lock (list)
            {
                list.RemoveAll(t => now - t > PSyncRetryWindow);
                if (list.Count >= PSyncMaxAutoRetries)
                {
                    Logger.LogWarning("PSync: missing files for {pair} ({why}); auto re-download cap reached ({cap} per {min} min)", Pair.PairUIDName, why, PSyncMaxAutoRetries, (int)PSyncRetryWindow.TotalMinutes);
                    return;
                }
                if (Interlocked.Exchange(ref _psyncRetryScheduled, 1) == 1) return;
                list.Add(now);
            }
            Logger.LogInformation("PSync: missing files for {pair} ({why}); re-downloading {n} file(s) and re-applying", Pair.PairUIDName, why, hashes.Length);
            var pair = Pair;
            _ = Task.Run(async () =>
            {
                try
                {
                    await PSyncForceRedownloadAsync(hashes, deleteFiles: false).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    pair.ReapplyCachedDataByUser();
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "PSync: missing-file retry failed");
                }
                finally
                {
                    Interlocked.Exchange(ref _psyncRetryScheduled, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "PSync: missing-file handling failed");
        }
    }
}
