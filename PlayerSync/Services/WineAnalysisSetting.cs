using Dalamud.Bindings.ImGui;

namespace MareSynchronos.Services;

/// <summary>
/// Wine only (macOS/Linux), 1.15.5.1202: optional switch to skip the Havok animation/skeleton bone check on your own
/// outgoing data. The check opens each modded .pap, writes a temp .hkx and parses it with Havok on the game thread, which
/// is a suspected freeze/crash source under Wine. Off by default (the check still runs); Windows ignores the setting.
/// When skipped, animations are sent without the bone-count check, so an incompatible animation is not filtered out.
/// </summary>
public static class WineAnalysisSetting
{
    public static bool ShouldSkip(bool isWine) => isWine && VierAIConfigStore.Current.WineSkipAnimationAnalysis;

    public static void Draw(bool isWine)
    {
        if (!isWine) return;
        var skip = VierAIConfigStore.Current.WineSkipAnimationAnalysis;
        if (ImGui.Checkbox("Skip animation bone check (Wine only)", ref skip))
        {
            VierAIConfigStore.Current.WineSkipAnimationAnalysis = skip;
            VierAIConfigStore.MarkDirty();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Skips the Havok skeleton analysis of your own modded animations before they are sent.\nTry this if the game freezes or crashes on macOS/Linux while your data is built.\nDownside: animations that don't fit other players' skeletons are no longer filtered out.");
    }
}