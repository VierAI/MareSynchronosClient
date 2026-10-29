using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MareSynchronos.Services;

namespace MareSynchronos.UI;

// 1.15.5.1217: memory/VRAM budget, hard blocklist, inline perf label, missing-file auto re-download.
public partial class SettingsUi
{
    private string _psyncBlockUidInput = string.Empty;
    private string _psyncBlockStatus = string.Empty;
    private int _psyncBlockSelected = -1;

    private static bool PSyncIntInput(string label, int value, int min, int max, out int result, float width = 120f)
    {
        result = value;
        ImGui.SetNextItemWidth(width * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt(label, ref result))
        {
            result = Math.Clamp(result, min, max);
            return true;
        }
        return false;
    }

    private void DrawPerformanceMemoryBudget()
    {
        var cfg = VierAIConfigStore.Current;

        _uiShared.BigText("Memory budget");
        var enabled = cfg.MemoryBudgetEnabled;
        if (ImGui.Checkbox("Enable memory budget", ref enabled))
        {
            cfg.MemoryBudgetEnabled = enabled;
            VierAIConfigStore.MarkDirty();
        }
        _uiShared.DrawHelpText("Off (default) changes nothing. When on, PlayerSync keeps the total approximate VRAM of synced players under a cap."
            + Environment.NewLine + "When over, it holds players in download-priority order: lowest tier first (Others, then Direct, Party, Friends),"
            + Environment.NewLine + "farthest first within a tier. Listed players are never held. Held players are shown unmodded and also stop getting your data,"
            + Environment.NewLine + "and both resume automatically once there is room again. Nothing is paused permanently.");

        using (ImRaii.Disabled(!enabled))
        {
            var mode = cfg.MemoryBudgetMode;
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo("Budget mode", ref mode, "Manual MB cap\0Auto (live GPU memory)\0"))
            {
                cfg.MemoryBudgetMode = mode;
                VierAIConfigStore.MarkDirty();
            }
            _uiShared.DrawHelpText("Auto reads your GPU's memory budget and usage (DXGI) and keeps PlayerSync under it with headroom."
                + Environment.NewLine + "On integrated GPUs, under Wine (Mac/Linux, incl. Apple Silicon unified memory), or if the GPU query fails, the manual cap is used.");
            if (PSyncGpuMemory.IsWine)
            {
                UiSharedService.ColorTextWrapped("Wine detected (Mac/Linux): Auto is not used here because memory is unified/shared; the manual cap applies.", ImGuiColors.DalamudYellow);
            }

            if (PSyncIntInput("Manual cap (MB)", cfg.MemoryBudgetManualMiB, 256, 262144, out var v)) { cfg.MemoryBudgetManualMiB = v; VierAIConfigStore.MarkDirty(); }
            if (PSyncIntInput("Crowded cap (MB, 0 = off)", cfg.MemoryBudgetCrowdedMiB, 0, 262144, out v)) { cfg.MemoryBudgetCrowdedMiB = v; VierAIConfigStore.MarkDirty(); }
            if (PSyncIntInput("Crowded when players nearby >=", cfg.MemoryBudgetCrowdedPlayers, 0, 1000, out v)) { cfg.MemoryBudgetCrowdedPlayers = v; VierAIConfigStore.MarkDirty(); }
            _uiShared.DrawHelpText("With this many players around (synced or not), the stricter crowded cap is used.");
            if (PSyncIntInput("Auto headroom (%)", cfg.MemoryBudgetHeadroomPercent, 0, 50, out v)) { cfg.MemoryBudgetHeadroomPercent = v; VierAIConfigStore.MarkDirty(); }
            if (PSyncIntInput("Auto headroom minimum (MB)", cfg.MemoryBudgetHeadroomMinMiB, 0, 16384, out v)) { cfg.MemoryBudgetHeadroomMinMiB = v; VierAIConfigStore.MarkDirty(); }
            if (PSyncIntInput("Resume margin (%)", cfg.MemoryBudgetMarginPercent, 0, 50, out v)) { cfg.MemoryBudgetMarginPercent = v; VierAIConfigStore.MarkDirty(); }
            if (PSyncIntInput("Minimum hold time (s)", cfg.MemoryBudgetMinHoldSeconds, 10, 600, out v)) { cfg.MemoryBudgetMinHoldSeconds = v; VierAIConfigStore.MarkDirty(); }
            _uiShared.DrawHelpText("A held player only comes back when they fit under the cap minus this margin, and nobody flips state faster than the hold time.");
        }

        ImGui.TextUnformatted("Active mode: " + PSyncCrowd.BudgetModeText);
        if (enabled && PSyncCrowd.BudgetCapBytes >= 0)
        {
            ImGui.TextUnformatted($"Synced players: {PSyncCrowd.BudgetUsedBytes / (1024 * 1024)} MB of {PSyncCrowd.BudgetCapBytes / (1024 * 1024)} MB, {PSyncCrowd.BudgetHeldCount} held");
        }
        if (enabled && cfg.MemoryBudgetMode == 1)
        {
            ImGui.TextUnformatted(PSyncGpuMemory.StatusText);
        }

        ImGui.Separator();
        _uiShared.BigText("Display & repair");
        var perfPane = cfg.PerfWarningInVisiblePane;
        if (ImGui.Checkbox("Show perf warning in visible pane", ref perfPane))
        {
            cfg.PerfWarningInVisiblePane = perfPane;
            VierAIConfigStore.MarkDirty();
        }
        _uiShared.DrawHelpText("Next to a visible pair, show only the value that is over your warning limit in Settings > Performance, e.g. \"277M\" (VRAM) or \"325kT\" (triangles). Yellow = over warning, red = over auto-pause. Nothing is shown when they are under your limits.");
        var perfChat = cfg.PerfWarningInChat;
        if (ImGui.Checkbox("Show perf warning in chat", ref perfChat))
        {
            cfg.PerfWarningInChat = perfChat;
            VierAIConfigStore.MarkDirty();
        }
        _uiShared.DrawHelpText("Print one chat line the first time a visible pair is over a warning limit (once per player per session, at most one line every 10 seconds).");
        var autoRedl = cfg.AutoRedownloadMissingFiles;
        if (ImGui.Checkbox("Re-download automatically when files are missing", ref autoRedl))
        {
            cfg.AutoRedownloadMissingFiles = autoRedl;
            VierAIConfigStore.MarkDirty();
        }
        _uiShared.DrawHelpText("When an apply finds missing files, PlayerSync re-downloads them and re-applies (at most 3 times per 10 minutes per player).");
        ImGui.TextUnformatted($"Downloads blocked by file safety checks this session: {PSyncFileSafety.BlockedCount}");

        ImGui.Separator();
        _uiShared.BigText("Blocklist");
        _uiShared.DrawHelpText("Blocked users: nothing from them is downloaded or applied and they stop getting your data. Separate from pause; kept across restarts.");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("UID / Vanity ID##psyncblock", ref _psyncBlockUidInput, 20);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_psyncBlockUidInput)))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Ban, "Block"))
            {
                // Audit M2: always store the UID. A vanity ID is resolved through your pairs; unknown text is kept as a UID.
                var input = _psyncBlockUidInput.Trim();
                var pair = _pairManager.GetPairByUID(input) ?? _pairManager.GetPairByAliasOrUID(input);
                var uid = pair?.UserData.UID ?? input;
                PSyncBlocklist.SetBlocked(uid, true, pair);
                _psyncBlockStatus = pair != null
                    ? $"Blocked {uid}"
                    : $"Stored \"{uid}\" as a UID (no pair with that UID or vanity ID right now; vanity IDs only work for current pairs)";
                _psyncBlockUidInput = string.Empty;
            }
        }
        _uiShared.DrawHelpText("A vanity ID is turned into that user's UID when you add it (it must be one of your pairs). Matching is by UID only, so changing a vanity ID does not unblock anyone. UIDs are case sensitive.");
        if (!string.IsNullOrEmpty(_psyncBlockStatus)) ImGui.TextWrapped(_psyncBlockStatus);

        List<string> blocked;
        lock (cfg.BlockedUIDs) blocked = cfg.BlockedUIDs.ToList();
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        using (var lb = ImRaii.ListBox("Blocked users"))
        {
            if (lb)
            {
                for (int i = 0; i < blocked.Count; i++)
                {
                    if (ImGui.Selectable(blocked[i] + "##psb" + i, _psyncBlockSelected == i)) _psyncBlockSelected = i;
                }
            }
        }
        using (ImRaii.Disabled(_psyncBlockSelected < 0 || _psyncBlockSelected >= blocked.Count))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Unblock selected"))
            {
                var uid = blocked[_psyncBlockSelected];
                _pairManager.TryGetPairByUserData(new MareSynchronos.API.Data.UserData(uid), out var pair);
                PSyncBlocklist.SetBlocked(uid, false, pair);
                _psyncBlockSelected = -1;
            }
        }
    }
}
