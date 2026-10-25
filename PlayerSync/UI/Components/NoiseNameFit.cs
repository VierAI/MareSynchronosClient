using Dalamud.Bindings.ImGui;

namespace MareSynchronos.UI.Components;

/// <summary>Fork: fixed-width name column for the standalone Noises window (no horizontal resizing as names change).</summary>
internal static class NoiseNameFit
{
    /// <summary>A typical long FFXIV name with average-width letters; the name column is sized to fit it.</summary>
    public const string SizingName = "Aaaaaaaaaaa Bbbbbbbbbbb";

    private const string Ellipsis = "\u2026";

    /// <summary>Width of the name column (ImGui measurement, so it follows the global font scale).</summary>
    public static float NameWidth() => ImGui.CalcTextSize(SizingName).X;

    /// <summary>The name clipped with an ellipsis to fit <paramref name="maxWidth"/>; Truncated tells the caller to show the full name in a tooltip.</summary>
    public static (string Text, bool Truncated) Fit(string name, float maxWidth)
    {
        if (ImGui.CalcTextSize(name).X <= maxWidth) return (name, false);
        var ellipsisWidth = ImGui.CalcTextSize(Ellipsis).X;
        var len = name.Length;
        while (len > 0 && ImGui.CalcTextSize(name[..len]).X + ellipsisWidth > maxWidth) len--;
        return (name[..len].TrimEnd() + Ellipsis, true);
    }
}