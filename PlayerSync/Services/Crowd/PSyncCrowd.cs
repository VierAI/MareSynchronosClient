using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.WebAPI.Files;
using System.Collections.Concurrent;

namespace MareSynchronos.Services;

/// <summary>
/// 1.15.5.1217: shared, read-mostly crowd state for visible pairs: "not drawn" (no draw object), distance to you,
/// memory-budget holds and the hard blocklist. Written by <see cref="PSyncCrowdService"/> (framework thread), read anywhere.
/// Keys are pair UIDs. Nothing here acts on the game by itself.
/// </summary>
public static class PSyncCrowd
{
    /// <summary>A pair must be without a draw object this long before it counts as not drawn (avoids redraw blips).</summary>
    public static readonly TimeSpan NotDrawnGrace = TimeSpan.FromSeconds(2);

    internal static readonly ConcurrentDictionary<string, DateTimeOffset> NotDrawnSince = new(StringComparer.Ordinal);
    internal static readonly ConcurrentDictionary<string, float> Distances = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, byte> PendingReapply = new(StringComparer.Ordinal);
    internal static readonly ConcurrentDictionary<string, DateTimeOffset> BudgetHeldSince = new(StringComparer.Ordinal);

    /// <summary>Human-readable budget mode line for the settings UI (e.g. "Auto (GPU)", "Manual (fallback: ...)").</summary>
    public static string BudgetModeText { get; internal set; } = "Off";
    public static long BudgetCapBytes { get; internal set; } = -1;
    public static long BudgetUsedBytes { get; internal set; } = -1;
    public static int BudgetHeldCount => BudgetHeldSince.Count;

    /// <summary>1.15.5.1218 (audit M2): matches the UID only. Vanity IDs are resolved to a UID when an entry is added.</summary>
    public static bool IsBlocked(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return false;
        var list = VierAIConfigStore.Current.BlockedUIDs;
        lock (list)
        {
            return list.Exists(u => string.Equals(u, uid, StringComparison.Ordinal));
        }
    }

    public static bool IsBlocked(Pair pair) => IsBlocked(pair.UserData.UID);

    public static bool IsNotDrawn(string uid) => NotDrawnSince.TryGetValue(uid, out var since) && DateTimeOffset.UtcNow - since >= NotDrawnGrace;

    public static bool IsBudgetHeld(string uid) => BudgetHeldSince.ContainsKey(uid);

    /// <summary>N1b: PlayerSync UIDs are 10 characters of A-Z and 0-9.</summary>
    public static bool LooksLikeUid(string s) => s.Length == 10 && s.All(ch => (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'));

    public static float DistanceOf(string uid) => Distances.TryGetValue(uid, out var d) ? d : float.MaxValue;

    /// <summary>Same tier function as the download queue (one source of truth).</summary>
    public static int TierOf(Pair pair) => DownloadPriorityQueue.Instance?.GetTier(pair) ?? PriorityTiers.Everyone;

    /// <summary>Not drawn + tier 3-4: downloads and applies wait until the game draws them. Tiers 0-2 pre-fetch anyway.</summary>
    public static bool DefersForNotDrawn(Pair pair) => IsNotDrawn(pair.UserData.UID) && TierOf(pair) >= PriorityTiers.DirectSync;

    /// <summary>True when nothing may download/apply for this pair right now (blocked, budget hold, or not drawn in tiers 3-4).</summary>
    public static bool HoldsApply(Pair pair)
    {
        try
        {
            return IsBlocked(pair) || IsBudgetHeld(pair.UserData.UID) || DefersForNotDrawn(pair);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when we should not push our own character data to this user (mutual hold: blocked or budget hold).</summary>
    public static bool HoldsPush(string uid)
    {
        try
        {
            return IsBlocked(uid) || IsBudgetHeld(uid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>One line on why a pair is waiting / what it is doing, for the tooltip and debug IPC. Null when nothing to say.</summary>
    public static string? DescribeWait(Pair pair)
    {
        try
        {
            var uid = pair.UserData.UID;
            var tier = TierOf(pair);
            var tierText = $"tier {tier} ({PriorityTiers.Name(tier)})";
            if (IsBlocked(pair)) return "Blocked: nothing from this user downloads or applies";
            if (pair.IsPaused) return "Paused" + (pair.GetPauseReason() is { Length: > 0 } r ? ": " + r : string.Empty);
            if (!pair.IsVisible) return null;
            if (IsBudgetHeld(uid)) return $"Paused by the memory budget ({tierText}); resumes when there is room";
            var notDrawn = IsNotDrawn(uid);
            var q = DownloadPriorityQueue.Instance;
            var (waiting, position, running) = q != null ? q.GetQueueInfo(pair) : (0, 0, 0);
            if (waiting > 0 || running > 0)
            {
                var s = $"Downloading {running} file(s), {waiting} waiting";
                if (position > 0) s += $" (queue position {position})";
                return s + $", {tierText}" + (notDrawn ? ", not drawn (pre-fetching)" : string.Empty);
            }
            if (notDrawn)
                return tier >= PriorityTiers.DirectSync
                    ? $"Not drawn by the game; downloads wait until it is ({tierText})"
                    : $"Not drawn by the game; files still pre-fetch ({tierText})";
            if (pair.HasDataApplicationTaskRunning) return $"Applying ({tierText})";
            if (!pair.HasCachedData) return $"Waiting for data ({tierText})";
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Set by PSyncCrowdService: the currently visible pairs (used to keep files that other pairs share).</summary>
    public static Func<List<Pair>>? VisiblePairsProvider { get; internal set; }

    internal static void Forget(string uid)
    {
        NotDrawnSince.TryRemove(uid, out _);
        Distances.TryRemove(uid, out _);
    }
}

/// <summary>1.15.5.1217: download file safety (extension allowlist, executable sniffing, origin host).</summary>
public static class PSyncFileSafety
{
    /// <summary>Game file types a synced character legitimately uses. Anything else is not written to the cache.</summary>
    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "tex", "atex", "mdl", "mtrl", "shpk", "scd", "avfx", "tmb", "pap", "sklb", "skp", "eid", "phyb", "pbd",
        "atch", "imc", "kdb", "bnk", "shcd", "uld", "lgb", "sgb", "est", "cmp", "eqp", "eqdp", "gmp", "rsp",
    };

    private static readonly ConcurrentDictionary<string, string> s_originByUid = new(StringComparer.Ordinal);
    private static int s_blockedCount;

    public static int BlockedCount => Volatile.Read(ref s_blockedCount);

    public static bool IsAllowedExtension(string? ext) => !string.IsNullOrEmpty(ext) && AllowedExtensions.Contains(ext.TrimStart('.'));

    /// <summary>True for content that starts like a program or archive (PE/MZ, ELF, Mach-O, script shebang, zip).</summary>
    public static bool LooksExecutable(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        if (data[0] == 'M' && data[1] == 'Z') return true;
        if (data[0] == 0x7F && data[1] == 'E' && data[2] == 'L' && data[3] == 'F') return true;
        if (data[0] == '#' && data[1] == '!') return true;
        if (data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4) return true;
        if ((data[0] == 0xFE && data[1] == 0xED && data[2] == 0xFA && (data[3] == 0xCE || data[3] == 0xCF))
            || ((data[0] == 0xCE || data[0] == 0xCF) && data[1] == 0xFA && data[2] == 0xED && data[3] == 0xFE)) return true;
        return false;
    }

    public static string OriginOf(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return "unknown";
        return u.Host;
    }

    public static void RecordOrigin(string? uid, string origin)
    {
        if (!string.IsNullOrEmpty(uid)) s_originByUid[uid] = origin;
    }

    public static string? OriginFor(string uid) => s_originByUid.TryGetValue(uid, out var o) ? o : null;

    public static void CountBlocked() => Interlocked.Increment(ref s_blockedCount);

    // 1.15.5.1218 (audit M1): an extension refusal is based on the pair-supplied game path, not on the bytes, so it only
    // applies to that pair. It never goes into the shared ForbiddenTransfers list (which also gates our own uploads).
    private static readonly ConcurrentDictionary<string, byte> s_refusedForPair = new(StringComparer.OrdinalIgnoreCase);

    private static string RefusalKey(string? uid, string hash) => (uid ?? string.Empty) + "|" + hash;

    public static void RefuseForPair(string? uid, string hash) => s_refusedForPair[RefusalKey(uid, hash)] = 0;

    public static bool IsRefusedForPair(string? uid, string hash) => !string.IsNullOrEmpty(hash) && s_refusedForPair.ContainsKey(RefusalKey(uid, hash));
}

/// <summary>1.15.5.1220: players the memory budget never holds (UIDs, stored in vierai-config.json).</summary>
public static class PSyncBudgetExempt
{
    public static bool IsExempt(string? uid)
    {
        if (string.IsNullOrEmpty(uid)) return false;
        var list = VierAIConfigStore.Current.MemoryBudgetExemptUIDs;
        lock (list) return list.Exists(u => string.Equals(u, uid, StringComparison.Ordinal));
    }

    public static void Toggle(Pair pair)
    {
        var uid = pair.UserData.UID;
        if (string.IsNullOrWhiteSpace(uid)) return;
        var list = VierAIConfigStore.Current.MemoryBudgetExemptUIDs;
        bool nowExempt;
        lock (list)
        {
            nowExempt = list.RemoveAll(u => string.Equals(u, uid, StringComparison.Ordinal)) == 0;
            if (nowExempt) list.Add(uid);
        }
        VierAIConfigStore.MarkDirty();
        if (nowExempt && PSyncCrowd.BudgetHeldSince.TryRemove(uid, out _)) PSyncCrowd.PendingReapply[uid] = 0;
    }
}

/// <summary>1.15.5.1217: hard per-UID blocklist (separate from pause, stored in vierai-config.json).</summary>
public static class PSyncBlocklist
{
    public static void SetBlocked(string uid, bool blocked, Pair? pair = null)
    {
        if (string.IsNullOrWhiteSpace(uid)) return;
        uid = uid.Trim();
        var list = VierAIConfigStore.Current.BlockedUIDs;
        lock (list)
        {
            list.RemoveAll(u => string.Equals(u, uid, StringComparison.Ordinal));
            if (blocked) list.Add(uid);
        }
        VierAIConfigStore.MarkDirty();
        if (pair == null) return;
        if (blocked)
        {
            if (pair.HasCachedData) _ = Task.Run(() => pair.ReleaseAppliedDataAsync("blocked"));
        }
        else
        {
            PSyncCrowd.PendingReapply[pair.UserData.UID] = 0;
        }
    }

    public static void Toggle(Pair pair) => SetBlocked(pair.UserData.UID, !PSyncCrowd.IsBlocked(pair), pair);
}
