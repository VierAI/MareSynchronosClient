namespace MareSynchronos.WebAPI.Files;

/// <summary>
/// The four download priority tiers. A lower number downloads first. A player's tier is the best (lowest)
/// tier they match. All tiers are always on.
/// </summary>
public static class PriorityTiers
{
    /// <summary>Players explicitly in the priority list (UID or vanity ID). Always enabled.</summary>
    public const int Listed = 0;
    /// <summary>People on the in-game (FFXIV) friends list.</summary>
    public const int InGameFriend = 1;
    /// <summary>Direct syncs: players you and they have paired with (both sides).</summary>
    public const int DirectSync = 2;
    /// <summary>Everyone else.</summary>
    public const int Everyone = 3;

    public static int Classify(bool listed, bool inGameFriend, bool directSync)
    {
        if (listed) return Listed;
        if (inGameFriend) return InGameFriend;
        if (directSync) return DirectSync;
        return Everyone;
    }

    public static string Name(int tier) => tier switch
    {
        Listed => "listed",
        InGameFriend => "in-game friend",
        DirectSync => "direct sync",
        _ => "everyone else",
    };
}
