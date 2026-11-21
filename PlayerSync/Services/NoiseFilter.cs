namespace MareSynchronos.Services;

/// <summary>1.15.6.203: Noises whitelist / blacklist words, matched against a sound's file name (not case-sensitive).</summary>
public enum NoiseFilterMatch
{
    None = 0,
    Whitelist = 1,
    Blacklist = 2,
}

public static class NoiseFilter
{
    /// <summary>ImGui ABGR colors: whitelist green, blacklist red (same colors as the old bpmloop / plap glow rules).</summary>
    public const uint WhitelistColor = 0xFF00FF00;
    public const uint BlacklistColor = 0xFF0000FF;

    // 1.15.6.211 (audit Q1): readers (data apply runs off the framework thread) use an immutable snapshot of both lists. The
    // editor never changes a list in place: it publishes a new list (copy-on-write), and the snapshot is rebuilt when the list
    // references change, so a reader never enumerates a list that is being modified.
    private sealed record Snapshot(List<string>? WhiteSrc, List<string>? BlackSrc, string[] White, string[] Black, string WhiteSig);
    private static Snapshot? s_snapshot;

    private static Snapshot Current()
    {
        var cfg = VierAIConfigStore.Current;
        var white = cfg.NoisesWhitelist;
        var black = cfg.NoisesBlacklist;
        var snap = Volatile.Read(ref s_snapshot);
        if (snap != null && ReferenceEquals(snap.WhiteSrc, white) && ReferenceEquals(snap.BlackSrc, black)) return snap;
        var w = Normalize(white);
        var b = Normalize(black);
        // 1.15.6.212 (audit R2): a failed copy gives an empty answer for this read only; it is not cached, so the next read retries.
        if (w == null || b == null) return new Snapshot(null, null, w ?? [], b ?? [], string.Empty);
        snap = new Snapshot(white, black, w, b, string.Join('|', w.Select(x => x.ToLowerInvariant())));
        Volatile.Write(ref s_snapshot, snap);
        return snap;
    }

    /// <summary>Trimmed non-empty words; null when the copy failed (list changed mid-copy).</summary>
    private static string[]? Normalize(List<string>? words)
    {
        if (words == null) return [];
        string[] copy;
        try { copy = words.ToArray(); }
        catch (ArgumentException) { return null; }
        return copy.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
    }

    /// <summary>1.15.6.211: replaces a list with a new one (never changes the old list), so readers keep a stable copy.</summary>
    public static void Publish(bool whitelist, List<string> words)
    {
        var cfg = VierAIConfigStore.Current;
        if (whitelist) cfg.NoisesWhitelist = words;
        else cfg.NoisesBlacklist = words;
        VierAIConfigStore.MarkDirty();
    }

    /// <summary>1.15.6.210: the whitelist wins when a file matches both lists (by design: the whitelist wins for that file).</summary>
    public static NoiseFilterMatch Match(string? gamePathOrFile)
    {
        if (string.IsNullOrEmpty(gamePathOrFile)) return NoiseFilterMatch.None;
        var file = Path.GetFileName(gamePathOrFile);
        var snap = Current();
        if (Contains(snap.White, file)) return NoiseFilterMatch.Whitelist;
        if (Contains(snap.Black, file)) return NoiseFilterMatch.Blacklist;
        return NoiseFilterMatch.None;
    }

    /// <summary>1.15.6.210: the file name contains a whitelisted word (kept and heard even while you have the player's sounds muted).</summary>
    public static bool IsWhitelisted(string? gamePathOrFile)
        => !string.IsNullOrEmpty(gamePathOrFile) && Contains(Current().White, Path.GetFileName(gamePathOrFile));

    /// <summary>1.15.6.210: the whitelist words, for change detection (reapply muted pairs when it changes).</summary>
    public static string WhitelistSignature() => Current().WhiteSig;

    /// <summary>1.15.6.211: the blacklist words, for change detection.</summary>
    public static string BlacklistSignature() => string.Join('|', Current().Black);

    private static bool Contains(string[] words, string file)
    {
        foreach (var w in words)
            if (file.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static List<string> DefaultWhitelist() => ["bpmloop"];
    public static List<string> DefaultBlacklist() => ["plap"];
}