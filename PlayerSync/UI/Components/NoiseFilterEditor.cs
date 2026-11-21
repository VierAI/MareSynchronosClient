using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MareSynchronos.Services;

namespace MareSynchronos.UI.Components;

/// <summary>1.15.6.203: whitelist (green) / blacklist (red) words and the auto-mute checkbox, shared by the Noises filter
/// dropdown and Settings > Game UI.</summary>
internal static class NoiseFilterEditor
{
    public static void Draw(UiSharedService ui, string idPrefix)
    {
        var cfg = VierAIConfigStore.Current;
        cfg.NoisesWhitelist ??= NoiseFilter.DefaultWhitelist();
        cfg.NoisesBlacklist ??= NoiseFilter.DefaultBlacklist();

        DrawList(ui, idPrefix + "white", "Whitelist (green highlight)", ImGuiColors.HealerGreen, cfg.NoisesWhitelist, true);
        // 1.15.6.211 (audit Q2)
        UiSharedService.ColorTextWrapped("Sound files whose name contains a whitelisted word (for example the default \"bpmloop\") play even" +
            " from players whose sounds you muted. Only a full pause, or the player turning their sounds off for you, stops them.", ImGuiColors.DalamudGrey);
        DrawList(ui, idPrefix + "black", "Blacklist (red highlight)", ImGuiColors.DalamudRed, cfg.NoisesBlacklist, false);
        if (!cfg.NoisesAutoMuteBlacklisted) // 1.15.6.206; 1.15.6.217: warning color and a one-click button
        {
            UiSharedService.ColorTextWrapped("Auto-mute is off: blacklisted sounds still play and are only highlighted.", ImGuiColors.DalamudOrange);
            if (ImGui.SmallButton("Turn on auto-mute##" + idPrefix + "automuteon"))
            {
                cfg.NoisesAutoMuteBlacklisted = true;
                VierAIConfigStore.MarkDirty();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Same as the \"Auto-mute players playing blacklisted sounds\" box below: blacklisted sound files are not applied" +
                    Environment.NewLine + "(silent from the first play) and players playing one are muted. Whitelisted sounds still play.");
        }
        else // 1.15.6.218: on (the default): a quiet note instead of the warning
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Blacklisted sounds are blocked (auto-mute on). Whitelisted sounds still play.");

        var autoMute = cfg.NoisesAutoMuteBlacklisted;
        if (ImGui.Checkbox("Auto-mute players playing blacklisted sounds##" + idPrefix, ref autoMute))
        {
            cfg.NoisesAutoMuteBlacklisted = autoMute;
            VierAIConfigStore.MarkDirty();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Silences every blacklisted sound file (file name contains a blacklisted word) of every player before it plays:" +
                Environment.NewLine + "their synced blacklisted sound files are not applied at all (silent from the first play), and other blacklisted" +
                Environment.NewLine + "paths are redirected to silence in their sync collection; their other sounds keep playing. A game sound path" +
                Environment.NewLine + "never seen before (not in their synced data or animations) can still be heard the first time; it is silent after that." +
                Environment.NewLine + "Off: the blacklist only highlights (red) and nothing is muted. A word is added when you press Enter or click Add." +
                Environment.NewLine +
                Environment.NewLine + "Also mutes a player (the same per-player mute as clicking their speaker) when a sound whose file name contains a" +
                Environment.NewLine + "blacklisted word loads for them. The mute lifts by itself once no blacklisted sound came from them for" +
                Environment.NewLine + "20 seconds and their emote loop has ended; a blacklisted sound after that mutes them again." +
                Environment.NewLine + "Mutes you set yourself are never lifted." +
                Environment.NewLine +
                Environment.NewLine + "Unmuting a player by hand (from any window) turns the blacklist off for that player until the plugin" +
                Environment.NewLine + "reloads: they are not auto-muted again and their blacklisted sounds play normally.");

        var autoUnmute = cfg.NoisesAutoUnmuteWhitelisted; // 1.15.6.204
        if (ImGui.Checkbox("Auto-unmute when a muted player plays a whitelisted sound##" + idPrefix, ref autoUnmute))
        {
            cfg.NoisesAutoUnmuteWhitelisted = autoUnmute;
            VierAIConfigStore.MarkDirty();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Whitelisted sounds already play from a player you muted (sounds only). This option goes further: when they play" +
                Environment.NewLine + "a whitelisted sound, ALL their sounds are turned back on once. It never mutes them again. A blacklisted sound from" +
                Environment.NewLine + "them in the last 15 seconds, or a blacklist auto-mute still in place, blocks it. Does not work for paused players," +
                Environment.NewLine + "or when they turned their sounds off for you themselves.");

        if (ImGui.Button("Reset lists##" + idPrefix))
        {
            NoiseFilter.Publish(true, NoiseFilter.DefaultWhitelist());
            NoiseFilter.Publish(false, NoiseFilter.DefaultBlacklist());
            s_pending.Clear(); // 1.15.6.209
            s_selected.Clear(); // 1.15.6.213
        }
    }

    // 1.15.6.213: each list is a list box like the other settings lists (add field + Add button, selectable entries,
    // "Delete selected"). Text typed in the add field is pending until Enter or Add; Escape discards it; clicking elsewhere
    // or closing the popup/window keeps it pending (shown again with a "not applied" note). Keyed by list, shared by the
    // Noises popup and Settings.
    private static readonly Dictionary<string, string> s_pending = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> s_selected = new(StringComparer.Ordinal); // 1.15.6.214 (E1): by word, not index

    // 1.15.6.211 (audit Q1): every change publishes a NEW list (copy-on-write); the shown list is never modified in place,
    // because the data apply reads the lists off the framework thread. 1.15.6.212 (R1): changes in one frame chain on the
    // list last published in that frame.
    private static void DrawList(UiSharedService ui, string id, string title, System.Numerics.Vector4 color, List<string> words, bool whitelist)
    {
        var list = whitelist ? "white" : "black";
        using var pid = ImRaii.PushId(id);
        var scale = ImGuiHelpers.GlobalScale;
        var width = 200 * scale;
        ImGui.TextColored(color, title);

        var current = words;
        // Entries saved blank by older versions (an "Add word" never filled in) are dropped.
        if (current.Exists(string.IsNullOrWhiteSpace))
        {
            current = current.Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
            NoiseFilter.Publish(whitelist, current);
        }

        var hasPending = s_pending.TryGetValue(list, out var pendingText);
        var text = hasPending ? pendingText! : string.Empty;
        ImGui.SetNextItemWidth(width);
        if (ImGui.InputTextWithHint("##add", "word in the file name", ref text, 64)) { s_pending[list] = text; hasPending = true; }
        var submit = false;
        if (ImGui.IsItemDeactivated() && hasPending)
        {
            if (ImGui.IsKeyDown(ImGuiKey.Escape)) { s_pending.Remove(list); hasPending = false; text = string.Empty; } // discard
            else if (ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter)) submit = true;
        }
        var fieldActive = ImGui.IsItemActive();
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(text)))
        {
            if (ui.IconTextButton(FontAwesomeIcon.Plus, "Add")) submit = true;
        }
        if (submit)
        {
            var word = text.Trim();
            if (word.Length > 0 && !current.Exists(w => string.Equals(w.Trim(), word, StringComparison.OrdinalIgnoreCase)))
            {
                current = [.. current, word];
                NoiseFilter.Publish(whitelist, current);
            }
            s_pending.Remove(list);
        }
        else if (hasPending && !fieldActive && !string.IsNullOrEmpty(text))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "not applied");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Press Enter in the field or click Add to add it; Escape in the field discards it.");
        }

        // 1.15.6.214 (audit E1): the selection is the word itself, so a change from the other editor (popup vs Settings) or a
        // reset can never point it at a different word; the first row with that exact text is the selected one.
        var selectedWord = s_selected.TryGetValue(list, out var sel) ? sel : null;
        var selected = selectedWord == null ? -1 : current.FindIndex(w => string.Equals(w, selectedWord, StringComparison.Ordinal));
        var rows = Math.Clamp(current.Count, 3, 6);
        if (ImGui.BeginListBox("##words", new System.Numerics.Vector2(width, (rows * ImGui.GetTextLineHeightWithSpacing()) + (ImGui.GetStyle().FramePadding.Y * 2))))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, color))
            {
                var textColor = ImGui.GetColorU32(ImGuiCol.Text);
                for (var i = 0; i < current.Count; i++)
                {
                    // 1.15.6.214 (audit E2): the label is only an ID; the word is drawn as plain text, so "##" in a word shows in full.
                    var pos = ImGui.GetCursorScreenPos();
                    if (ImGui.Selectable("##w" + i, selected == i, ImGuiSelectableFlags.DontClosePopups, new System.Numerics.Vector2(0, ImGui.GetTextLineHeight()))) selected = i;
                    ImGui.GetWindowDrawList().AddText(pos, textColor, current[i]);
                }
            }
            ImGui.EndListBox();
        }
        using (ImRaii.Disabled(selected < 0))
        {
            if (ui.IconTextButton(FontAwesomeIcon.Trash, "Delete selected") && selected >= 0)
            {
                var next = new List<string>(current);
                next.RemoveAt(selected);
                NoiseFilter.Publish(whitelist, next);
                selected = -1;
            }
        }
        if (selected >= 0 && selected < current.Count) s_selected[list] = current[selected];
        else s_selected.Remove(list);
    }
}
