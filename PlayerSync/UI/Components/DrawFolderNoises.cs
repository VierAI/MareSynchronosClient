using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.UI.Handlers;
using MareSynchronos.WebAPI;
using System.Numerics;

namespace MareSynchronos.UI.Components;

/// <summary>
/// "Noises" folder in the main window pair list (between Visible Stages and Visible), 1.15.5.910.
/// Lists visible, unpaused pairs that are making noise (or whose speaker is still fading), newest first.
/// Draws nothing while nobody is. Hover does a temporary focus, right-click targets (see <see cref="PairHoverTargeter"/>).
/// </summary>
public class DrawFolderNoises
{
    private const string _tagId = "noises";

    private readonly TagHandler _tagHandler;
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly PairHoverTargeter _hover;
    private readonly ApiController _apiController;
    private readonly MareMediator _mediator;
    private bool _wasHovered;
    private readonly NoiseRowOrder _rowOrder = new();
    private bool _sectionHovered;

    internal DrawFolderNoises(TagHandler tagHandler, UiSharedService uiSharedService, PairManager pairManager, MareMediator mediator, MareConfigService configService, ApiController apiController)
    {
        _apiController = apiController;
        _mediator = mediator;
        _tagHandler = tagHandler;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _hover = new PairHoverTargeter(mediator, configService);
    }

    private readonly MutedRowLinger _mutedLinger = new(); // fork: muted rows drop off 30 s after the last hover

    public List<Pair> GetListedPairs(PairManager pairManager, DateTimeOffset now)
    {
        var rows = new List<Pair>();
        var visible = pairManager.GetVisiblePairs();
        _mutedLinger.Prune(visible);
        foreach (var pair in visible)
        {
            if (pair.UserPair == null || pair.IsPaused || pair.SoundStateSince == null) continue;
            if (!SoundActivity.IsListedOrMuted(pair, now)) continue;
            if (!_mutedLinger.Keep(pair.UserData.UID, pair.UserPair.OwnPermissions.IsDisableSounds(), now)) continue;
            rows.Add(pair);
        }
        rows.Sort((a, b) => Nullable.Compare(b.SoundStateSince, a.SoundStateSince));
        return rows;
    }

    public void Draw()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = _rowOrder.Update(GetListedPairs(_pairManager, now), _sectionHovered); // fork: stable order (NoiseRowOrder.cs)
        var sectionStart = ImGui.GetCursorScreenPos();

        _hover.BeginFrame();
        if (rows.Count == 0)
        {
            _sectionHovered = false;
            _hover.EndFrame();
            return;
        }

        using (ImRaii.PushId("noises"))
        {
            using (ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.FrameBgHovered), _wasHovered))
            using (ImRaii.Child("noises_folder", new Vector2(UiSharedService.GetWindowContentRegionWidth() - ImGui.GetCursorPosX(), ImGui.GetFrameHeight())))
            {
                var expanderIcon = _tagHandler.IsTagOpen(_tagId) ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight;
                ImGui.AlignTextToFramePadding();
                _uiSharedService.IconText(expanderIcon);
                if (ImGui.IsItemClicked())
                {
                    _tagHandler.SetTagOpen(_tagId, !_tagHandler.IsTagOpen(_tagId));
                }

                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.VolumeUp);

                ImGui.SameLine();
                ImGui.TextUnformatted($"[{rows.Count}] Noises");
            }
            _wasHovered = ImGui.IsItemHovered();

            ImGui.Separator();

            if (_tagHandler.IsTagOpen(_tagId))
            {
                using (ImRaii.PushIndent(_uiSharedService.GetIconSize(FontAwesomeIcon.EllipsisV).X + ImGui.GetStyle().ItemSpacing.X, false))
                {
                    foreach (var pair in rows)
                    {
                        DrawRow(pair, now);
                    }
                    ImGui.Separator();
                }
            }
        }

        // Whole section (header + rows) hover, used next frame to hold removals
        var sectionEnd = ImGui.GetCursorScreenPos();
        _sectionHovered = ImGui.IsMouseHoveringRect(sectionStart, new Vector2(sectionStart.X + UiSharedService.GetWindowContentRegionWidth(), sectionEnd.Y));

        _hover.EndFrame();
    }

    private void DrawRow(Pair pair, DateTimeOffset now)
    {
        using var id = ImRaii.PushId(pair.UserData.UID);
        var rowMin = ImGui.GetCursorScreenPos();
        var rowMax = new Vector2(rowMin.X + UiSharedService.GetWindowContentRegionWidth() - ImGui.GetCursorPosX(), rowMin.Y + ImGui.GetFrameHeight());

        var since = now - pair.SoundStateSince!.Value;
        ImGui.AlignTextToFramePadding();
        MutedRowLinger.DrawSpeaker(_uiSharedService, pair, pair.UserPair!.OwnPermissions.IsDisableSounds(), since);

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        var name = string.IsNullOrWhiteSpace(pair.PlayerName) ? pair.UserData.AliasOrUID : pair.PlayerName;
        ImGui.TextUnformatted(name); // fork: no "(muted)" label, the mute button shows it

        // Same mute/pause buttons as the Noises window, right-aligned.
        var endX = ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth();
        var buttons = NoiseRowButtons.Draw(pair, endX, _uiSharedService, _apiController, _mediator);

        var rowHovered = ImGui.IsMouseHoveringRect(rowMin, rowMax) && ImGui.IsWindowHovered();
        if (rowHovered) _mutedLinger.Touch(pair.UserData.UID, now);
        _hover.Row(pair, rowHovered, overButton: buttons.Hovered);
    }
}