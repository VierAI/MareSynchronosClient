using MareSynchronos.API.Data.Extensions;
using MareSynchronos.PlayerData.Pairs;
using System.Collections.Concurrent;

namespace MareSynchronos.Services;

/// <summary>
/// Fork: unmuting a pair runs the same full reapply as "Reapply last data" (forced, debounce bypassed, emote restarted
/// from the start), so their sound comes back without a manual reapply. Full reapplies are at most one per pair per
/// <see cref="Cooldown"/>; an unmute inside the cooldown is not dropped but runs once when the cooldown ends (if the
/// pair is still unmuted then), so a quick mute/unmute toggle still restores the sound.
/// </summary>
public static class UnmuteSoundRestore
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> _deferred = new(StringComparer.Ordinal);

    /// <summary>Reapplies after a permission change; full user-style reapply when this change unmuted the pair.</summary>
    public static void Reapply(Pair pair, bool unmuted)
    {
        if (!unmuted) { pair.ReapplyCachedData(); return; }
        var uid = pair.UserData.UID;
        var now = DateTimeOffset.UtcNow;
        if (!_last.TryGetValue(uid, out var last) || now - last >= Cooldown)
        {
            _last[uid] = now;
            // 1225 (audit 8): entries older than the cooldown no longer matter
            if (_last.Count > 256)
            {
                foreach (var kv in _last)
                {
                    if (now - kv.Value >= Cooldown && !string.Equals(kv.Key, uid, StringComparison.Ordinal)) _last.TryRemove(kv.Key, out _);
                }
            }
            pair.ReapplyCachedDataByUser();
            return;
        }

        pair.ReapplyCachedData();
        if (!_deferred.TryAdd(uid, 0)) return; // one deferred restore per pair
        var wait = Cooldown - (now - last);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(wait).ConfigureAwait(false);
                if (pair.IsPaused || (pair.UserPair?.OwnPermissions.IsDisableSounds() ?? true)) return;
                _last[uid] = DateTimeOffset.UtcNow;
                pair.ReapplyCachedDataByUser();
            }
            finally { _deferred.TryRemove(uid, out _); }
        });
    }
}