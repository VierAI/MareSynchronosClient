using Microsoft.Extensions.Logging;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MareSynchronos.MareConfiguration;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.UI.ModernUi;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.API.Data;
using System.Numerics;

namespace MareSynchronos.UI;

// Fork (VierAI 1413): Noises-style pair request window. Transparent (no background, title bar or border) until hovered;
// each request shows the requester's avatar and name, and hovering reveals the pair/group ID and icon buttons
// (check = accept, X = dismiss, ban = ignore player). The buttons call the same handlers as before.
public class PairingRequestsUi : WindowMediatorSubscriberBase
{
    private const float FadeSeconds = 0.2f;

    private readonly PairInviteManager _pairRequestManager;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly PairManager _pairManager;
    private readonly UiTheme _theme;
    private readonly UiSharedService _uiSharedService;
    private readonly MareProfileManager _profileManager;
    private readonly Dictionary<string, IDalamudTextureWrap?> _avatars = new(StringComparer.Ordinal);
    private readonly ILogger<PairingRequestsUi> _logger;
    private bool _userClickedSomething = false;
    private bool _windowHovered;
    private float _hoverFade;
    private int _pushedColors;
    private Vector4 _baseText = new(1, 1, 1, 1);

    public PairingRequestsUi(ILogger<PairingRequestsUi> logger, MareMediator mediator,
        PerformanceCollectorService performanceCollectorService, PlayerPerformanceConfigService playerPerformanceConfig,
        UiTheme theme, PairInviteManager pairRequestManager, ServerConfigurationManager serverConfigurationManager, PairManager pairManager,
        UiSharedService uiSharedService, MareProfileManager profileManager)
        : base(logger, mediator, "PlayerSync Pair Requests", performanceCollectorService)
    {
        _logger = logger;
        _pairRequestManager = pairRequestManager;
        _serverConfigurationManager = serverConfigurationManager;
        _pairManager = pairManager;
        _theme = theme;
        _uiSharedService = uiSharedService;
        _profileManager = profileManager;

        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing;

        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen = false);

        SizeConstraints = new()
        {
            MinimumSize = new(260, 0),
            MaximumSize = new(600, 600)
        };
    }

    public override void Update()
    {
        var step = ImGui.GetIO().DeltaTime / FadeSeconds;
        var target = _windowHovered ? 1f : 0f;
        _hoverFade = _hoverFade < target ? Math.Min(target, _hoverFade + step) : Math.Max(target, _hoverFade - step);
    }

    public override unsafe void PreDraw()
    {
        UiSharedService.CenterOnOpen(true);
        base.PreDraw();

        _pushedColors = 0;
        _baseText = *ImGui.GetStyleColorVec4(ImGuiCol.Text);
        ImGuiCol[] faded = [ImGuiCol.WindowBg, ImGuiCol.Border, ImGuiCol.TitleBg, ImGuiCol.TitleBgActive, ImGuiCol.TitleBgCollapsed];
        foreach (var col in faded)
        {
            var c = *ImGui.GetStyleColorVec4(col);
            c.W *= _hoverFade;
            ImGui.PushStyleColor(col, c);
            _pushedColors++;
        }
    }

    public override void PostDraw()
    {
        if (_pushedColors > 0) ImGui.PopStyleColor(_pushedColors);
        _pushedColors = 0;
        base.PostDraw();
    }

    protected override void DrawInternal()
    {
        // basically close the window if the user accepted/rejected the final requets
        // but don't close if they just toggle open the empty window manually
        if (_pairRequestManager.ReceivedPendingCount == 0
            && _pairRequestManager.ReceivedGroupInviteCount == 0
            && _userClickedSomething)
            IsOpen = false;

        _windowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.ChildWindows);

        using var windowStyle = _theme.PushWindowStyle();
        using var text = ImRaii.PushColor(ImGuiCol.Text, _baseText);
        DrawPendingRequests();
    }

    private void DrawPendingRequests()
    {
        var pendingIncomingRequests = _pairRequestManager.GetPendingRequests();
        var pendingIncomingInvites = _pairRequestManager.GetPendingGroupInvites();

        if (!pendingIncomingRequests.Any() && !pendingIncomingInvites.Any())
        {
            ImGui.TextUnformatted("No pending pair requests.");
            return;
        }

        foreach (var request in pendingIncomingRequests)
        {
            var requestorUid = request.Requestor.UID;
            var requestorName = _serverConfigurationManager.GetPendingRequestNameForIdent(request.RequestorIdent) ?? request.Requestor.AliasOrUID;

            DrawEntry(requestorUid, request.Requestor, requestorName, requestorUid, requestorUid,
                accept: () => _pairRequestManager.SendPairRequest(new UserData(requestorUid)),
                dismiss: () => _pairRequestManager.SendPairRejection(new UserData(requestorUid)),
                ignore: () =>
                {
                    _pairRequestManager.SendPairRejection(new UserData(requestorUid));
                    _serverConfigurationManager.AddPairingRequestBlacklistUid(requestorUid);
                },
                dismissTooltip: "Silently drop this request without notifying the player who requested to pair.");
        }

        foreach (var invite in pendingIncomingInvites)
        {
            var groupName = invite.GroupAliasOrGID;
            var requestorUid = invite.InvitingUser?.UID ?? String.Empty;
            var requestorName = _pairManager.GetPairByUID(requestorUid)?.PlayerName ?? invite.InvitingUser?.AliasOrUID ?? "Unknown";
            var inviteId = invite.RequestId;

            DrawEntry($"{inviteId}-{requestorUid}", invite.InvitingUser, requestorName, groupName, invite.Group.AliasOrGID,
                accept: () => _pairRequestManager.SendGroupInviteJoin(inviteId),
                dismiss: () => _pairRequestManager.SendRejectGroupInvite(inviteId),
                ignore: () =>
                {
                    _pairRequestManager.SendRejectGroupInvite(inviteId);
                    _serverConfigurationManager.AddPairingRequestBlacklistUid(requestorUid);
                },
                dismissTooltip: "Silently drop this request without notifying the player who sent the invite.");
        }
    }

    private void DrawEntry(string id, UserData? requestor, string name, string shownId, string copyId,
        Action accept, Action dismiss, Action ignore, string dismissTooltip)
    {
        using var pushId = ImRaii.PushId(id);
        var scale = ImGuiHelpers.GlobalScale;
        var avatarSize = new Vector2(48f * scale, 48f * scale);
        var spacingX = ImGui.GetStyle().ItemSpacing.X;

        ImGui.BeginGroup();
        DrawAvatar(requestor, avatarSize);
        ImGui.SameLine();

        var textX = ImGui.GetCursorPosX();
        var startY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(startY + (_windowHovered ? 0f : (avatarSize.Y - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.TextUnformatted(name);

        if (_windowHovered)
        {
            ImGui.SetCursorPosX(textX);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                ImGui.TextUnformatted(shownId);
            if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked()) ImGui.SetClipboardText(copyId);
            UiSharedService.AttachToolTip("Click to copy");

            ImGui.SetCursorPosX(textX);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.Check))
                {
                    accept();
                    _userClickedSomething = true;
                }
            }
            UiSharedService.AttachToolTip("Accept");

            ImGui.SameLine(0, spacingX);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Times))
            {
                dismiss();
                _userClickedSomething = true;
            }
            UiSharedService.AttachToolTip("Dismiss" + UiSharedService.TooltipSeparator + dismissTooltip);

            ImGui.SameLine(0, spacingX);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.Ban))
                {
                    ignore();
                    _userClickedSomething = true;
                }
            }
            UiSharedService.AttachToolTip("Ignore Player" + UiSharedService.TooltipSeparator + "Ignoring a player will block their requests to you." + UiSharedService.TooltipSeparator +
                "Unblock requests in Settings -> Sync Settings -> Pair Requests.");
        }
        else
        {
            // Same-height placeholder so the window doesn't jump when hovered.
            ImGui.SetCursorPosY(startY);
            ImGui.Dummy(new Vector2(1f, avatarSize.Y));
        }
        ImGui.EndGroup();
        ImGui.Spacing();
    }

    private void DrawAvatar(UserData? user, Vector2 size)
    {
        IDalamudTextureWrap? tex = null;
        if (user != null && !string.IsNullOrEmpty(user.UID))
        {
            if (!_avatars.TryGetValue(user.UID, out tex))
            {
                var profile = _profileManager.GetMareProfile(user);
                if (_profileManager.HasOwnPicture(profile) && _profileManager.IsProfileMarkedNsfwOrAllowed(profile, user))
                {
                    try { tex = _uiSharedService.LoadImage(profile.ImageData.Value); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Could not load pair request avatar"); tex = null; }
                    _avatars[user.UID] = tex;
                }
                else if (!_profileManager.IsLoading(profile))
                {
                    _avatars[user.UID] = null; // no picture: icon fallback, don't retry every frame
                }
            }
        }

        if (tex != null)
        {
            ImGui.Image(tex.Handle, size);
            return;
        }

        var start = ImGui.GetCursorPos();
        var iconSize = _uiSharedService.GetIconSize(FontAwesomeIcon.UserCircle);
        ImGui.SetCursorPos(start + (size - iconSize) * 0.5f);
        _uiSharedService.IconText(FontAwesomeIcon.UserCircle);
        ImGui.SetCursorPos(start);
        ImGui.Dummy(size);
    }

    public override void OnClose()
    {
        _userClickedSomething = false;
        foreach (var t in _avatars.Values) t?.Dispose();
        _avatars.Clear();

        base.OnClose();
    }
}
