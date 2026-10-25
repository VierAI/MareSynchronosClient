using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;

namespace MareSynchronos.UI.Components;

/// <summary>
/// Fork: muted rows in the Noises window and the main-window Noises section (VierAI 2026-10-24).
/// - A muted player stays listed for <see cref="LingerSeconds"/> after the row was last hovered, then drops off
///   (replaces the earlier rule that kept muted players listed while visible). Removal still waits while the list is hovered
///   (NoiseRowOrder), so rows never move under the mouse.
/// - Once dropped, a muted player stays off the list until unmuted or unloaded.
/// - A muted row shows a red mute icon instead of the activity speaker.
/// </summary>
internal sealed class MutedRowLinger
{
    public const double LingerSeconds = 30;

    private readonly Dictionary<string, DateTimeOffset> _since = new(StringComparer.Ordinal);
    private readonly HashSet<string> _gone = new(StringComparer.Ordinal);

    /// <summary>For a muted pair: true while it should still be listed. For an unmuted pair: forgets any muted state, returns true.</summary>
    public bool Keep(string uid, bool muted, DateTimeOffset now)
    {
        if (!muted)
        {
            _since.Remove(uid);
            _gone.Remove(uid);
            return true;
        }
        if (_gone.Contains(uid)) return false;
        if (!_since.TryGetValue(uid, out var since))
        {
            _since[uid] = now;
            return true;
        }
        if ((now - since).TotalSeconds <= LingerSeconds) return true;
        _since.Remove(uid);
        _gone.Add(uid);
        return false;
    }

    /// <summary>Row hovered: restart the muted-row timer.</summary>
    public void Touch(string uid, DateTimeOffset now)
    {
        if (_since.ContainsKey(uid)) _since[uid] = now;
    }

    /// <summary>Forget players that are no longer visible (a muted player who comes back gets a fresh timer).</summary>
    public void Prune(IEnumerable<Pair> visible)
    {
        if (_since.Count == 0 && _gone.Count == 0) return;
        var set = visible.Select(p => p.UserData.UID).ToHashSet(StringComparer.Ordinal);
        _since.Keys.Where(u => !set.Contains(u)).ToList().ForEach(u => _since.Remove(u));
        _gone.RemoveWhere(u => !set.Contains(u));
    }

    /// <summary>Row speaker: a red mute icon when muted, otherwise the activity speaker.</summary>
    public static void DrawSpeaker(UiSharedService ui, Pair pair, bool muted, TimeSpan since)
    {
        if (muted)
        {
            ui.IconText(FontAwesomeIcon.VolumeMute, ImGuiColors.DalamudRed);
            UiSharedService.AttachToolTip("Muted: you disabled sound sync with this player.");
            return;
        }
        ui.IconText(SoundActivity.SpeakerIcon(pair), SoundActivity.IconColor(pair, since));
        UiSharedService.AttachToolTip(SoundActivity.Describe(pair, since));
    }
}