using Dalamud.Bindings.ImGui;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using System.Numerics;

namespace MareSynchronos.UI.Components;

/// <summary>
/// 1706: glow behind a Noises row's name while the player is playing a matching sound. 1.15.6.203: blacklist words (red) and
/// whitelist words (green) from the Noises filter win, then any custom NoiseGlowRules. "Playing" = the pair is Active and
/// the matching file is ANY sound loaded in the current active period (or in the last 10 seconds), not just the newest one:
/// a DJ effect that loads several sounds (bpmloop not first) still glows.
/// </summary>
internal static class NoiseGlow
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(10);
    private const float EdgeAlpha = 0.12f; // 1.15.6.205: was 0.33 with 8 neighbours
    private static readonly TimeSpan ActiveGrace = TimeSpan.FromSeconds(SoundActivity.LoopStartGraceSeconds + 1);

    public static uint? ColorFor(Pair pair, SoundActivityTracker tracker, DateTimeOffset now)
    {
        if (!SoundActivity.IsActive(pair)) return null;
        var sounds = tracker.GetRecentSounds(pair); // newest first
        if (sounds.Count == 0) return null;
        var activeSince = pair.SoundStateSince ?? now;
        var white = false;
        uint? custom = null;
        var rules = VierAIConfigStore.Current.NoiseGlowRules;
        for (var i = 0; i < sounds.Count; i++)
        {
            var (gamePath, at, _) = sounds[i];
            if (i > 0 && now - at > RecentWindow && at < activeSince - ActiveGrace) continue;
            switch (NoiseFilter.Match(gamePath))
            {
                case NoiseFilterMatch.Blacklist: return NoiseFilter.BlacklistColor;
                case NoiseFilterMatch.Whitelist: white = true; break;
            }
            if (custom == null && rules != null)
            {
                var file = Path.GetFileName(gamePath);
                foreach (var rule in rules)
                {
                    if (rule.Enabled && !string.IsNullOrWhiteSpace(rule.Match) && file.Contains(rule.Match, StringComparison.OrdinalIgnoreCase))
                    { custom = rule.Color; break; }
                }
            }
        }
        if (white) return NoiseFilter.WhitelistColor;
        return custom;
    }

    /// <summary>Soft rounded rect plus a faint 1 px colored outline of the text, drawn before the real text so it sits behind it.
    /// 1.15.6.205: the outline is toned down (the green background is fine, the outline was too much): 4 neighbours instead
    /// of 8 and a lower alpha, for both the whitelist (green) and blacklist (red) colors. The background fill is unchanged.</summary>
    public static void Draw(Vector2 pos, string text, uint color)
    {
        var dl = ImGui.GetWindowDrawList();
        var c = ImGui.ColorConvertU32ToFloat4(color);
        var alpha = ImGui.GetStyle().Alpha;
        var size = ImGui.CalcTextSize(text);
        var soft = ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, c.W * 0.15f * alpha));
        dl.AddRectFilled(pos - new Vector2(1.5f, 0.5f), pos + size + new Vector2(1.5f, 0.5f), soft, 3f); // 1.15.6.203: half size and intensity
        var edge = ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, c.W * EdgeAlpha * alpha));
        dl.AddText(pos + new Vector2(-1, 0), edge, text);
        dl.AddText(pos + new Vector2(1, 0), edge, text);
        dl.AddText(pos + new Vector2(0, -1), edge, text);
        dl.AddText(pos + new Vector2(0, 1), edge, text);
    }
}