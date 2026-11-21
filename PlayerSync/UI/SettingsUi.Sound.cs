using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;

namespace MareSynchronos.UI;

// Game UI options for "who is making noise" (name tag speaker and the Noises window).
public partial class SettingsUi
{
    // 1706: rules that glow a Noises row's name while the player plays a matching sound.
    private void DrawNoiseGlowRules()
    {
        var store = MareSynchronos.Services.VierAIConfigStore.Current;
        store.NoiseGlowRules ??= MareSynchronos.Services.NoiseGlowRule.Defaults();
        var rules = store.NoiseGlowRules;
        ImGui.TextUnformatted("Custom Noises name glow colors");
        _uiShared.DrawHelpText("A player's name in the Noises window glows in the rule's color while they play a sound whose file name contains the text (not case-sensitive)." + Environment.NewLine +
            "The first matching rule wins. The Noises filter whitelist (green) and blacklist (red) come first; no custom rules by default.");
        var remove = -1;
        for (var i = 0; i < rules.Count; i++)
        {
            using var id = ImRaii.PushId("noiseglow" + i);
            var rule = rules[i];
            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled)) { rule.Enabled = enabled; MareSynchronos.Services.VierAIConfigStore.MarkDirty(); }
            ImGui.SameLine();
            var match = rule.Match ?? string.Empty;
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputText("##match", ref match, 64)) { rule.Match = match; MareSynchronos.Services.VierAIConfigStore.MarkDirty(); }
            ImGui.SameLine();
            var color = ImGui.ColorConvertU32ToFloat4(rule.Color);
            if (ImGui.ColorEdit4("##color", ref color, ImGuiColorEditFlags.NoInputs)) { rule.Color = ImGui.ColorConvertFloat4ToU32(color); MareSynchronos.Services.VierAIConfigStore.MarkDirty(); }
            ImGui.SameLine();
            if (_uiShared.IconButton(FontAwesomeIcon.Trash)) remove = i;
        }
        if (remove >= 0) { rules.RemoveAt(remove); MareSynchronos.Services.VierAIConfigStore.MarkDirty(); }
        if (ImGui.Button("Add glow rule")) { rules.Add(new MareSynchronos.Services.NoiseGlowRule { Match = "", Color = 0xFFFFAA00 }); MareSynchronos.Services.VierAIConfigStore.MarkDirty(); }
        ImGui.SameLine();
        if (ImGui.Button("Clear glow rules")) { store.NoiseGlowRules = []; MareSynchronos.Services.VierAIConfigStore.MarkDirty(); }
    }

    private void DrawSoundActivitySettings()
    {
        var nameTagSpeaker = _configService.Current.NamePlatePairedSoundIndicator;
        if (ImGui.Checkbox("Show green speaker on name tags of players making noise", ref nameTagSpeaker))
        {
            _configService.Current.NamePlatePairedSoundIndicator = nameTagSpeaker;
            _configService.Save();
            Mediator.Publish(new RedrawNameplateMessage());
        }
        _uiShared.DrawHelpText("Adds a green speaker next to the name of a visible pair while modded sounds are loading for them." + Environment.NewLine +
            "It shows while the player is in a looping emote and a modded sound file was loaded for them (vanilla sounds are not seen)." + Environment.NewLine +
            "It may take a moment to show up or disappear in game." + Environment.NewLine +
            "Sound detection is off entirely while Debug > \"Disable sound indicators\" is checked (then this, the Noises window and the Pair Analysis Sound column show nothing).");

        var showNoiseMakers = _configService.Current.ShowNoiseMakersWindow;
        if (ImGui.Checkbox("Show Noises window", ref showNoiseMakers))
        {
            _configService.Current.ShowNoiseMakersWindow = showNoiseMakers;
            _configService.Save();
            Mediator.Publish(new NoiseMakersSettingChangedMessage());
        }
        _uiShared.DrawHelpText("Opens a small window listing visible players who are making noise, with quick mute and pause buttons." + Environment.NewLine +
            "It sizes itself to its rows, stays invisible until you hover it, and hides while the main window is open (the main window has a Noises folder instead)." + Environment.NewLine +
            "Closing that window turns this setting off.");

        var hoverOnly = MareSynchronos.Services.VierAIConfigStore.Current.NoisesOnlyShowOnHover;
        if (ImGui.Checkbox("Only show window on hover", ref hoverOnly))
        {
            MareSynchronos.Services.VierAIConfigStore.Current.NoisesOnlyShowOnHover = hoverOnly;
            MareSynchronos.Services.VierAIConfigStore.MarkDirty();
        }
        _uiShared.DrawHelpText("On: the Noises window's background, title and buttons stay invisible until you hover it (names still show)." + Environment.NewLine +
            "Off (default): the window stays fully visible.");

        // 1.15.6.202
        var hideGameMuted = MareSynchronos.Services.VierAIConfigStore.Current.NoisesHideGameMutedChannels;
        if (ImGui.Checkbox("Hide noises on game sound channels you muted", ref hideGameMuted))
        {
            MareSynchronos.Services.VierAIConfigStore.Current.NoisesHideGameMutedChannels = hideGameMuted;
            MareSynchronos.Services.VierAIConfigStore.MarkDirty();
        }
        _uiShared.DrawHelpText("On (default): a player drops out of the Noises window and folder while every sound they just played is on a channel" +
            " you muted or set to 0 in the game's Sound settings (Sound Effects, Voice, System Sounds, BGM, Ambient), or while Master is muted." + Environment.NewLine +
            "They come back as soon as you unmute that channel. The channel comes from the sound file itself (its bus number), so a mod" +
            " can play on System Sounds even if it sounds like an effect. Sounds whose channel is unknown are always shown." + Environment.NewLine +
            "Hover a name in the Noises window to see each sound's channel.");

        // 1.15.6.203: Noises filter (same editor as the filter button in the Noises window title bar).
        ImGui.TextUnformatted("Noises filter");
        _uiShared.DrawHelpText("Words matched against sound file names (not case-sensitive). Whitelist words glow green, blacklist words glow red" +
            " (blacklist wins). Also in the Noises window: the filter button in its title bar.");
        using (ImRaii.PushIndent())
        {
            MareSynchronos.UI.Components.NoiseFilterEditor.Draw(_uiShared, "set");
        }

        DrawNoiseGlowRules();

        var expire = Math.Clamp(MareSynchronos.Services.VierAIConfigStore.Current.NoisesRecentExpireMinutes, 1, 60);
        ImGui.SetNextItemWidth(250 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Recent sounds expire after (minutes)", ref expire, 1, 60))
        {
            MareSynchronos.Services.VierAIConfigStore.Current.NoisesRecentExpireMinutes = Math.Clamp(expire, 1, 60);
            MareSynchronos.Services.VierAIConfigStore.MarkDirty();
        }
        _uiShared.DrawHelpText("Recently played sounds in the Noises tooltips drop off after this many minutes. 1 to 60, default 5.");

        var fadeSeconds = Math.Clamp(MareSynchronos.Services.VierAIConfigStore.Current.NoisesFadeSeconds, 10, 300);
        ImGui.SetNextItemWidth(250 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Noise fade time (seconds)", ref fadeSeconds, 10, 300))
        {
            MareSynchronos.Services.VierAIConfigStore.Current.NoisesFadeSeconds = Math.Clamp(fadeSeconds, 10, 300);
            MareSynchronos.Services.VierAIConfigStore.MarkDirty();
        }
        _uiShared.DrawHelpText("How long a noise entry takes to fade out after the sound stops, then it drops from the Noises window and folder." + Environment.NewLine +
            "Hovered rows still never move or drop; muted rows go after the delay below once you stop hovering.");

        var mutedHide = Math.Clamp(MareSynchronos.Services.VierAIConfigStore.Current.NoisesMutedHideSeconds, 5, 60);
        ImGui.SetNextItemWidth(250 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Muted noise hide delay (seconds)", ref mutedHide, 5, 60))
        {
            MareSynchronos.Services.VierAIConfigStore.Current.NoisesMutedHideSeconds = Math.Clamp(mutedHide, 5, 60);
            MareSynchronos.Services.VierAIConfigStore.MarkDirty();
        }
        _uiShared.DrawHelpText("How long a muted player stays in the Noises window after your mouse last hovered their row. Hovering the row resets the timer. 5 to 60 seconds, default 15.");
    }
}