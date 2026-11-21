namespace MareSynchronos.WebAPI.Files;

/// <summary>
/// 1.15.6.213: per-tier slot split for transfers and pair loading (by design). Each of the five priority tiers
/// (<see cref="PriorityTiers"/>: listed, in-game friend, party/alliance, direct sync, everyone else) gets floor(total / 5)
/// base slots (any remainder goes to the top tier). Lending cascades top-down: each tier lends half (rounded down) of what
/// it has, including what it borrowed, to the tier below, so with 20 slots: 2 / 3 / 4 / 4 / 7.
/// A tier with more work than its share claws back as many slots as it needs from the tiers below it (top tier first).
/// A tier's share is reserved even while it is idle, so its work starts at once when it arrives. Running work is never
/// interrupted: a tier over its share just starts nothing new until its running work drops below it. The shares always
/// add up to the total. Below 5 slots there is no split: every slot goes to the best waiting tier (the old strict order).
/// </summary>
public static class TierSlots
{
    public const int Tiers = 5;

    /// <summary>The idle shares after lending (null when total &lt; 5: no split).</summary>
    public static int[]? Shares(int total)
    {
        if (total < Tiers) return null;
        var b = total / Tiers;
        var cur = b + (total - (b * Tiers));
        var keep = new int[Tiers];
        for (var i = 0; i < Tiers - 1; i++)
        {
            var lend = cur / 2;
            keep[i] = cur - lend;
            cur = b + lend;
        }
        keep[Tiers - 1] = cur;
        return keep;
    }

    /// <summary>Slots each tier may hold right now, given its demand (running + waiting). Null when total &lt; 5.</summary>
    public static int[]? Allowances(int total, ReadOnlySpan<int> demand)
    {
        var keep = Shares(total);
        if (keep == null) return null;
        var allow = new int[Tiers];
        var remaining = total;
        for (var i = 0; i < Tiers; i++)
        {
            var want = Math.Max(keep[i], i < demand.Length ? demand[i] : 0);
            allow[i] = Math.Min(remaining, want);
            remaining -= allow[i];
        }
        return allow;
    }

    public static int Clamp(int tier) => Math.Clamp(tier, 0, Tiers - 1);

    /// <summary>Short text for logs and tooltips, e.g. "2/3/4/4/7".</summary>
    public static string Describe(int total)
    {
        var s = Shares(total);
        return s == null ? "no split (fewer than 5)" : string.Join('/', s);
    }
}
