using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MareSynchronos.MareConfiguration.Configurations;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using System.Numerics;

namespace MareSynchronos.UI;

// "Priority" tab of the Transfers settings: which players' downloads get free download slots first.
public partial class SettingsUi
{
    // UI wording lives here so it is easy to change.
    private const string PriorityTitle = "Download Priority";
    private const string PriorityDescription = "Downloads are ordered in four tiers: 0 players in the list below, 1 in-game friends, " +
        "2 direct syncs, 3 everyone else. A player gets the best tier they match. Whenever a download slot frees up it goes to the best " +
        "waiting tier: tier 0 can use every slot, tier 1 whatever tier 0 isn't using, and so on. Downloads that already started are never paused.";
    private const string PriorityLabelAddUid = "Add UID/Vanity ID";
    private const string PriorityLabelAddTarget = "Add target";
    private const string PriorityLabelDeleteSelected = "Delete selected UID";
    private const string PriorityLabelTargetNotPair = "Your target is not a visible PlayerSync pair.";
    private const string PriorityTier0Text = "Tier 0: players in the list above.";
    private const string PriorityTier1Text = "Tier 1: in-game friends";
    private const string PriorityHelpInGameFriends = "People on your in-game (FFXIV) friends list. Only the real friends list counts, not PlayerSync pairs. " +
        "The list is read from the game every few seconds, so it can take a moment after login.";
    private const string PriorityTier2Text = "Tier 2: direct syncs";
    private const string PriorityHelpDirectSyncs = "Players you are directly paired with on both sides (not just syncshell members).";
    private const string PriorityTier3Text = "Tier 3: everyone else.";

    private string _uidToAddForPriority = string.Empty;
    private int _selectedPriorityEntry = -1;
    private string _priorityTargetMessage = string.Empty;

    private void AddPriorityUid(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return;
        uid = uid.Trim();
        var list = _playerPerformanceConfigService.Current.UIDsToPrioritize;
        if (!list.Contains(uid, StringComparer.Ordinal))
        {
            list.Add(uid);
            PriorityConfigChanged();
        }
    }

    private void PriorityConfigChanged()
    {
        _playerPerformanceConfigService.Save();
        // Lets the download queue hand out free slots again right away.
        Mediator.Publish(new DownloadLimitChangedMessage());
        // Re-tier downloads already waiting or running (e.g. someone added to the list while visible).
        Mediator.Publish(new PriorityChangedMessage());
    }

    private void DrawTransfersPriority()
    {
        var config = _playerPerformanceConfigService.Current;

        _uiShared.BigText(PriorityTitle);
        ImGuiHelpers.ScaledDummy(2);
        UiSharedService.TextWrapped(PriorityDescription);
        ImGui.Dummy(new Vector2(10));

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##priorityuid", ref _uidToAddForPriority, 20);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_uidToAddForPriority)))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, PriorityLabelAddUid))
            {
                AddPriorityUid(_uidToAddForPriority);
                _uidToAddForPriority = string.Empty;
            }
        }
        ImGui.SameLine();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Crosshairs, PriorityLabelAddTarget))
        {
            var target = _dalamudUtilService.SoftTargetAddress != nint.Zero ? _dalamudUtilService.SoftTargetAddress : _dalamudUtilService.TargetAddress;
            var pair = target == nint.Zero ? null : _pairManager.GetVisiblePairs().FirstOrDefault(u => u.Address == target);
            if (pair != null)
            {
                _priorityTargetMessage = string.Empty;
                AddPriorityUid(pair.UserData.UID);
            }
            else
            {
                _priorityTargetMessage = PriorityLabelTargetNotPair;
            }
        }
        if (!string.IsNullOrEmpty(_priorityTargetMessage))
            UiSharedService.ColorTextWrapped(_priorityTargetMessage, ImGuiColors.DalamudYellow);
        _uiShared.DrawHelpText("Hint: UIDs are case sensitive. \"Add target\" uses your current (soft) target.");

        var priorityList = config.UIDsToPrioritize;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        using (var lb = ImRaii.ListBox("Priority UIDs"))
        {
            if (lb)
            {
                for (int i = 0; i < priorityList.Count; i++)
                {
                    if (ImGui.Selectable(priorityList[i] + "##prio" + i, _selectedPriorityEntry == i))
                        _selectedPriorityEntry = i;
                }
            }
        }
        using (ImRaii.Disabled(_selectedPriorityEntry < 0 || _selectedPriorityEntry >= priorityList.Count))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, PriorityLabelDeleteSelected))
            {
                priorityList.RemoveAt(_selectedPriorityEntry);
                _selectedPriorityEntry = -1;
                PriorityConfigChanged();
            }
        }

        ImGui.Dummy(new Vector2(10));
        ImGui.Text("Tiers (best first)");
        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted(PriorityTier0Text);
            ImGui.TextUnformatted(PriorityTier1Text);
            ImGui.SameLine();
            ImGui.TextDisabled($"({InGameFriendService.CurrentCount} friends found)");
            _uiShared.DrawHelpText(PriorityHelpInGameFriends);
            ImGui.TextUnformatted(PriorityTier2Text);
            _uiShared.DrawHelpText(PriorityHelpDirectSyncs);
            ImGui.TextUnformatted(PriorityTier3Text);
        }

    }
}