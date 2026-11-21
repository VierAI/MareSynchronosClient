using MareSynchronos.Services;

namespace MareSynchronos.UI.Components;

/// <summary>
/// 1.15.6.205: players the blacklist auto-mute muted (never ones you muted by hand).
/// - Tracked: uid -> time of the last banned sound request seen from them (the mute itself, then any blacklisted request
///   that still arrives while they are muted).
/// - Lifted by <see cref="NoiseMakersUi"/> once no banned request came for <see cref="QuietSeconds"/> and their emote loop
///   has ended (whichever is later). A banned sound after the lift mutes them again.
/// - Any manual mute change (speaker click, or the pair found unmuted by something else such as the permission window)
///   drops the pair from tracking and stops auto-muting them this session: what you set by hand is never undone.
/// - The tracked uids are saved in the fork config, so an auto-mute still lifts after a plugin reload.
/// Called on the framework thread; locked anyway.
/// </summary>
internal static class NoiseAutoMute
{
    public const double QuietSeconds = 20;

    private static readonly object Lock = new();
    /// <summary>The permission change is only seen locally after the server echoes it; until then an auto-muted pair still
    /// reads as unmuted, which must not count as "you unmuted it by hand".</summary>
    public const double ServerEchoGraceSeconds = 15;

    private sealed class Entry
    {
        public DateTimeOffset MutedAt;
        public DateTimeOffset LastBanned;
    }

    private static readonly Dictionary<string, Entry> s_lastBanned = new(StringComparer.Ordinal);
    private static readonly HashSet<string> s_manualOverride = new(StringComparer.Ordinal);
    private static bool s_loaded;

    private static void EnsureLoaded()
    {
        if (s_loaded) return;
        s_loaded = true;
        var now = DateTimeOffset.UtcNow;
        foreach (var uid in VierAIConfigStore.Current.NoisesAutoMutedUids ?? [])
            if (!string.IsNullOrEmpty(uid)) s_lastBanned[uid] = new Entry { MutedAt = now, LastBanned = now }; // restart the quiet timer after a reload
    }

    private static void Persist()
    {
        VierAIConfigStore.Current.NoisesAutoMutedUids = [.. s_lastBanned.Keys.OrderBy(k => k, StringComparer.Ordinal)];
        VierAIConfigStore.MarkDirty();
        VierAIConfigStore.TrySave(out _);
    }

    public static bool IsTracked(string uid)
    {
        lock (Lock) { EnsureLoaded(); return s_lastBanned.ContainsKey(uid); }
    }

    /// <summary>You changed this pair's mute by hand this session (any manual unmute counts). Until the plugin reloads the
    /// blacklist is off for them: they are not auto-muted for a blacklisted sound and their blacklisted sound files are not
    /// silenced (1.15.6.208: blacklisted sounds play normally after a manual unmute, and the whole player is not auto-muted
    /// again until the plugin reloads).</summary>
    public static bool IsManuallyOverridden(string uid)
    {
        lock (Lock) return s_manualOverride.Contains(uid);
    }

    // 1.15.6.209 (N5): the last sound UNMUTE this client sent per pair, and whether the plugin sent it (auto-mute lift,
    // whitelist auto-unmute) or you did (any window). Recorded in ApiController.SetBulkPermissions, the one place every
    // permission change is sent from, so a later manual unmute always replaces an earlier automatic one that was never echoed.
    private static readonly Dictionary<string, (bool Auto, DateTimeOffset At)> s_unmuteSent = new(StringComparer.Ordinal);
    private const double UnmuteEchoSeconds = 30;
    [ThreadStatic] private static bool t_autoSend;

    /// <summary>1.15.6.209: wrap the plugin's own permission sends; SetBulkPermissions records them synchronously as automatic.</summary>
    public static IDisposable AutoSendScope()
    {
        t_autoSend = true;
        return new AutoScope();
    }

    private sealed class AutoScope : IDisposable
    {
        public void Dispose() => t_autoSend = false;
    }

    /// <summary>1.15.6.209: a permission change that unmutes this pair is being sent (called from SetBulkPermissions).</summary>
    public static void OnUnmuteSent(string uid)
    {
        lock (Lock) s_unmuteSent[uid] = (t_autoSend, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 1.15.6.208 (N1): the server confirmed that YOUR sound permission for this pair went from muted to unmuted (any UI path:
    /// Noises speaker, main list, permission window, bulk menu, analysis viewer, another client). Called from
    /// PairManager.UpdateSelfPairPermissions before the mute redirects are refreshed.
    /// 1.15.6.209 (N5): decided by the last unmute this client sent for the pair (within <see cref="UnmuteEchoSeconds"/>): sent
    /// by the plugin = automatic, ignored; sent by you, or none (another client) = manual.
    /// </summary>
    public static void OnUnmuteConfirmed(string uid)
    {
        lock (Lock)
        {
            if (s_unmuteSent.Remove(uid, out var sent) && sent.Auto && (DateTimeOffset.UtcNow - sent.At).TotalSeconds <= UnmuteEchoSeconds) return;
        }
        OnManualChange(uid, unmuting: true);
    }

    /// <summary>The plugin just muted this pair for a banned sound.</summary>
    public static void Track(string uid, DateTimeOffset now)
    {
        lock (Lock) { EnsureLoaded(); s_lastBanned[uid] = new Entry { MutedAt = now, LastBanned = now }; Persist(); }
    }

    /// <summary>A banned sound request from a tracked (still muted) pair: restart the quiet timer.</summary>
    public static void BannedSeen(string uid, DateTimeOffset now)
    {
        lock (Lock)
        {
            EnsureLoaded();
            if (s_lastBanned.TryGetValue(uid, out var e) && now > e.LastBanned) e.LastBanned = now;
        }
    }

    /// <summary>Manual mute/unmute: stop tracking and stop auto-muting this pair.
    /// 1.15.6.207: a manual UNMUTE of any pair (auto-muted or not) also marks it overridden (see <see cref="IsManuallyOverridden"/>).</summary>
    public static void OnManualChange(string uid, bool unmuting = false)
    {
        lock (Lock)
        {
            EnsureLoaded();
            if (s_lastBanned.Remove(uid)) { s_manualOverride.Add(uid); Persist(); }
            else if (unmuting) s_manualOverride.Add(uid);
        }
    }

    /// <summary>Stop tracking (lifted, or found unmuted). <paramref name="manual"/>: unmuted by something other than the plugin.</summary>
    public static void Forget(string uid, bool manual)
    {
        lock (Lock)
        {
            EnsureLoaded();
            if (!s_lastBanned.Remove(uid)) return;
            if (manual) s_manualOverride.Add(uid);
            Persist();
        }
    }

    public static List<(string Uid, DateTimeOffset MutedAt, DateTimeOffset LastBanned)> Snapshot()
    {
        lock (Lock) { EnsureLoaded(); return [.. s_lastBanned.Select(kv => (kv.Key, kv.Value.MutedAt, kv.Value.LastBanned))]; }
    }
}
