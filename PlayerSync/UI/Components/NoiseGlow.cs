using Dalamud.Bindings.ImGui;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using System.Numerics;

namespace MareSynchronos.UI.Components;

/// <summary>
/// 1706: glow behind a Noises row's name while the player is playing a sound that matches one of the configured rules
/// (VierAIConfig.NoiseGlowRules, defaults plap = red, bpmloop = green). "Playing" = the pair's sound state is Active and
/// the matching file is the newest one loaded for them or was loaded in the last 10 seconds.
/// </summary>
internal static class NoiseGlow
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(10);

    public static uint? ColorFor(Pair pair, SoundActivityTracker tracker, DateTimeOffset now)
    {
        var rules = VierAIConfigStore.Current.NoiseGlowRules;
        if (rules == null || rules.Count == 0 || !SoundActivity.IsActive(pair)) return null;
        var sounds = tracker.GetRecentSounds(pair.UserData.UID); // newest first
        for (var i = 0; i < sounds.Count; i++)
        {
            var (gamePath, at) = sounds[i];
            if (i > 0 && now - at > RecentWindow) break;
            var file = Path.GetFileName(gamePath);
            foreach (var rule in rules)
            {
                if (rule.Enabled && !string.IsNullOrWhiteSpace(rule.Match) && file.Contains(rule.Match, StringComparison.OrdinalIgnoreCase))
                    return rule.Color;
            }
        }
        return null;
    }

    /// <summary>Soft rounded rect plus a 1 px colored outline of the text, drawn before the real text so it sits behind it.</summary>
    public static void Draw(Vector2 pos, string text, uint color)
    {
        var dl = ImGui.GetWindowDrawList();
        var c = ImGui.ColorConvertU32ToFloat4(color);
        var alpha = ImGui.GetStyle().Alpha;
        var size = ImGui.CalcTextSize(text);
        var soft = ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, c.W * 0.30f * alpha));
        dl.AddRectFilled(pos - new Vector2(3, 1), pos + size + new Vector2(3, 1), soft, 4f);
        var edge = ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, c.W * 0.65f * alpha));
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0) dl.AddText(pos + new Vector2(dx, dy), edge, text);
    }
}