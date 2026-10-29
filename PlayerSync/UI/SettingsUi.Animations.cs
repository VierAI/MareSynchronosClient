using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace MareSynchronos.UI;

public partial class SettingsUi
{
    /// <summary>Fork: own "Animations" tab under Interface (moved out of the sound settings, VierAI 2026-10-25).</summary>
    private void DrawAnimationSettings()
    {
        _uiShared.BigText("Animations");
        ImGuiHelpers.ScaledDummy(2);
        // Fork 1.15.5.1210: Auto sync nearby animations (Services/AutoAnimationSync.cs)
        var autoSync = MareSynchronos.Services.VierAIConfigStore.Current.AutoSyncNearbyAnimations;
        if (ImGui.Checkbox("Auto sync nearby animations", ref autoSync))
        {
            MareSynchronos.Services.VierAIConfigStore.Current.AutoSyncNearbyAnimations = autoSync;
            MareSynchronos.Services.VierAIConfigStore.MarkDirty();
        }
        _uiShared.DrawHelpText("When you and a synced player within the radius are both in a looping emote or pose, both animations restart together (no redraw)." + Environment.NewLine +
            "Happens when the second of you starts the emote, and again after either of you is redrawn. Only on your screen; the other player sees it synced only if they use this too." + Environment.NewLine +
            "If you also use EmoteSync, prefer one of the two: both restart animations, and running both may make an emote restart twice.");
        using (ImRaii.Disabled(!autoSync))
        {
            var radius = MareSynchronos.Services.VierAIConfigStore.Current.AutoSyncNearbyRadius;
            ImGui.SetNextItemWidth(250 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
            if (ImGui.SliderFloat("Sync radius (m)", ref radius, 0.1f, 3f, "%.1f"))
            {
                MareSynchronos.Services.VierAIConfigStore.Current.AutoSyncNearbyRadius = Math.Clamp(radius, 0.1f, 3f);
                MareSynchronos.Services.VierAIConfigStore.MarkDirty();
            }
            var intro = MareSynchronos.Services.VierAIConfigStore.Current.AutoSyncIntroReplay;
            if (ImGui.Checkbox("Also replay emote intros (experimental)", ref intro))
            {
                MareSynchronos.Services.VierAIConfigStore.Current.AutoSyncIntroReplay = intro;
                MareSynchronos.Services.VierAIConfigStore.MarkDirty();
            }
            _uiShared.DrawHelpText("For emotes with a start animation that leads into a loop: both of you replay the start together instead of one being on the start and the other already looping." + Environment.NewLine +
                "Untested: the game may snap back to the loop.");
        }
    }
}
