using MareSynchronos.PlayerData.Pairs;

namespace MareSynchronos.UI.Components;

/// <summary>
/// Fork: stable row order for the Noises window and the main-window Noises section, so rows never move under the mouse.
/// - First-seen order: a new noise maker is appended at the bottom; no re-sorting by recency, state, mute or pause.
/// - While hovered nothing is removed (removals wait until the mouse leaves); new rows may still be appended.
/// - When not hovered, rows no longer listed are dropped and the rest keep their relative order.
/// </summary>
internal sealed class NoiseRowOrder
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, Pair> _pairs = new(StringComparer.Ordinal);

    /// <param name="listed">Pairs that should be listed right now (any order).</param>
    /// <param name="hovered">Mouse is over the whole window or section.</param>
    public List<Pair> Update(IEnumerable<Pair> listed, bool hovered)
    {
        var current = new Dictionary<string, Pair>(StringComparer.Ordinal);
        foreach (var pair in listed) current[pair.UserData.UID] = pair;

        // Several new rows in one frame: oldest sound first, so the newest ends up at the bottom.
        foreach (var pair in current.Values.Where(p => !_pairs.ContainsKey(p.UserData.UID)).OrderBy(p => p.SoundStateSince ?? DateTimeOffset.MaxValue))
        {
            _order.Add(pair.UserData.UID);
        }
        foreach (var kv in current) _pairs[kv.Key] = kv.Value;

        if (!hovered)
        {
            _order.RemoveAll(uid => !current.ContainsKey(uid));
            foreach (var uid in _pairs.Keys.Where(u => !current.ContainsKey(u)).ToList()) _pairs.Remove(uid);
        }

        return _order.Select(uid => _pairs[uid]).ToList();
    }
}