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
using MareSynchronos.UI.Components;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.API.Data;
using System.Numerics;

namespace MareSynchronos.UI;

// Fork (VierAI 1413/1417): Noises-style pair request list. Transparent (no background, title bar or border) until hovered.
// 1417: one line per request: small avatar, name (syncshell for invites), then check = accept, X = dismiss, ban = ignore
// player on the same line. The old "PlayerSync Pair Requests" pill (PairingRequestsNoticeUi) is gone: this window opens
// by itself when a new request arrives. Hovering a row does the temporary hover focus (restored when the hover ends),
// right-clicking a row (not a button) targets the player, left-clicking the name copies the ID.
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
    private readonly PairHoverTargeter _hover;
    private int _lastPendingCount;
    private bool _autoOpened;
    private bool _inGpose;
    private const float AvatarSize = 36f; // 1417: was 48

    public PairingRequestsUi(ILogger<PairingRequestsUi> logger, MareMediator mediator,
        PerformanceCollectorService performanceCollectorService, PlayerPerformanceConfigService playerPerformanceConfig,
        MareConfigService configService, UiTheme theme, PairInviteManager pairRequestManager, ServerConfigurationManager serverConfigurationManager, PairManager pairManager,
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

        _hover = new PairHoverTargeter(mediator, configService);

        Mediator.Subscribe<GposeStartMessage>(this, (_) => { _inGpose = true; IsOpen = false; });
        Mediator.Subscribe<GposeEndMessage>(this, (_) =>
        {
            _inGpose = false;
            if (PendingCount > 0) { IsOpen = true; _autoOpened = true; }
        });

        SizeConstraints = new()
        {
            MinimumSize = new(260, 0),
            MaximumSize = new(600, 600)
        };
    }

    private int PendingCount => _pairRequestManager.ReceivedPendingCount + _pairRequestManager.ReceivedGroupInviteCount;

    // 1417: replaces the pill step. Runs every frame, open or not: a new request opens the list by itself.
    public override void PreOpenCheck()
    {
        var count = PendingCount;
        if (count > _lastPendingCount && !_inGpose && !IsOpen)
        {
            IsOpen = true;
            _autoOpened = true;
        }
        _lastPendingCount = count;
        base.PreOpenCheck();
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
        if (PendingCount == 0 && (_userClickedSomething || _autoOpened))
            IsOpen = false;

        _windowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.ChildWindows);

        using var windowStyle = _theme.PushWindowStyle();
        using var text = ImRaii.PushColor(ImGuiCol.Text, _baseText);
        DrawPendingRequests();
    }

    private readonly record struct Row(string Key, UserData? Requestor, ulong ContentId, string? Ident, string Name, string? Group,
        string ShownId, string CopyId, Action Accept, Action Dismiss, Action Ignore, string DismissTooltip);

    private readonly List<Row> _rows = [];

    private void DrawPendingRequests()
    {
        _rows.Clear();
        foreach (var request in _pairRequestManager.GetPendingRequests())
        {
            var requestorUid = request.Requestor.UID;
            var requestorName = _serverConfigurationManager.GetPendingRequestNameForIdent(request.RequestorIdent) ?? request.Requestor.AliasOrUID;
            var pair = _pairManager.GetPairByUID(requestorUid);
            _rows.Add(new Row(requestorUid, request.Requestor, pair?.ContendId ?? 0, request.RequestorIdent, requestorName, null,
                requestorUid, requestorUid,
                Accept: () => _pairRequestManager.SendPairRequest(new UserData(requestorUid)),
                Dismiss: () => _pairRequestManager.SendPairRejection(new UserData(requestorUid)),
                Ignore: () =>
                {
                    _pairRequestManager.SendPairRejection(new UserData(requestorUid));
                    _serverConfigurationManager.AddPairingRequestBlacklistUid(requestorUid);
                },
                DismissTooltip: "Silently drop this request without notifying the player who requested to pair."));
        }

        foreach (var invite in _pairRequestManager.GetPendingGroupInvites())
        {
            var requestorUid = invite.InvitingUser?.UID ?? String.Empty;
            var pair = _pairManager.GetPairByUID(requestorUid);
            var requestorName = !string.IsNullOrEmpty(pair?.PlayerName) ? pair!.PlayerName! : invite.InvitingUser?.AliasOrUID ?? "Unknown";
            var inviteId = invite.RequestId;
            _rows.Add(new Row($"{inviteId}-{requestorUid}", invite.InvitingUser, pair?.ContendId ?? 0,
                string.IsNullOrEmpty(pair?.HashedCID) ? null : pair!.HashedCID, requestorName, invite.GroupAliasOrGID,
                invite.GroupAliasOrGID, invite.Group.AliasOrGID,
                Accept: () => _pairRequestManager.SendGroupInviteJoin(inviteId),
                Dismiss: () => _pairRequestManager.SendRejectGroupInvite(inviteId),
                Ignore: () =>
                {
                    _pairRequestManager.SendRejectGroupInvite(inviteId);
                    _serverConfigurationManager.AddPairingRequestBlacklistUid(requestorUid);
                },
                DismissTooltip: "Silently drop this request without notifying the player who sent the invite."));
        }

        if (_rows.Count == 0)
        {
            ImGui.TextUnformatted("No pending pair requests.");
            return;
        }

        // Every row is as wide as the widest one; the buttons are right-aligned inside that width (Noises window style).
        var scale = ImGuiHelpers.GlobalScale;
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var buttonsWidth = ButtonsWidth();
        var widest = 0f;
        foreach (var r in _rows)
        {
            var w = ImGui.CalcTextSize(r.Name).X;
            if (r.Group != null) w += spacingX + ImGui.CalcTextSize(GroupLabel(r.Group)).X;
            widest = Math.Max(widest, w);
        }
        var minWidth = 260f * scale - ImGui.GetStyle().WindowPadding.X * 2f;
        var rowWidth = Math.Max(minWidth, AvatarSize * scale + spacingX + widest + spacingX * 2f + buttonsWidth);

        _hover.BeginFrame();
        foreach (var r in _rows) DrawRow(r, rowWidth, buttonsWidth);
        _hover.EndFrame();
    }

    private static string GroupLabel(string group) => "(Syncshell " + group + ")";

    private float ButtonsWidth()
    {
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        return _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Check).X + spacingX
            + _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Times).X + spacingX
            + _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Ban).X;
    }

    private void DrawRow(Row r, float rowWidth, float buttonsWidth)
    {
        using var pushId = ImRaii.PushId(r.Key);
        var scale = ImGuiHelpers.GlobalScale;
        var avatar = new Vector2(AvatarSize * scale, AvatarSize * scale);
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var frameH = ImGui.GetFrameHeight();
        var rowHeight = Math.Max(avatar.Y, frameH);
        var start = ImGui.GetCursorPos();
        var rowMin = ImGui.GetCursorScreenPos();
        var rowMax = new Vector2(rowMin.X + rowWidth, rowMin.Y + rowHeight);
        var midY = start.Y + (rowHeight - frameH) * 0.5f;
        var overButton = false;

        ImGui.SetCursorPosY(start.Y + (rowHeight - avatar.Y) * 0.5f);
        DrawAvatar(r.Requestor, avatar);

        ImGui.SameLine();
        ImGui.SetCursorPosY(midY);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(r.Name);
        if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) ImGui.SetClipboardText(r.CopyId);
        UiSharedService.AttachToolTip(r.ShownId + UiSharedService.TooltipSeparator + "Left-click: copy ID" + Environment.NewLine + "Right-click: target");

        if (r.Group != null)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(midY);
            ImGui.AlignTextToFramePadding();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                ImGui.TextUnformatted(GroupLabel(r.Group));
        }

        ImGui.SameLine(start.X + rowWidth - buttonsWidth);
        ImGui.SetCursorPosY(midY);
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Check))
            {
                r.Accept();
                _userClickedSomething = true;
            }
        }
        overButton |= ImGui.IsItemHovered();
        UiSharedService.AttachToolTip("Accept");

        ImGui.SameLine(0, spacingX);
        if (_uiSharedService.IconButton(FontAwesomeIcon.Times))
        {
            r.Dismiss();
            _userClickedSomething = true;
        }
        overButton |= ImGui.IsItemHovered();
        UiSharedService.AttachToolTip("Dismiss" + UiSharedService.TooltipSeparator + r.DismissTooltip);

        ImGui.SameLine(0, spacingX);
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Ban))
            {
                r.Ignore();
                _userClickedSomething = true;
            }
        }
        overButton |= ImGui.IsItemHovered();
        UiSharedService.AttachToolTip("Ignore Player" + UiSharedService.TooltipSeparator + "Ignoring a player will block their requests to you." + UiSharedService.TooltipSeparator +
            "Unblock requests in Settings -> Sync Settings -> Pair Requests.");

        // Keep the full row height (avatar may be taller than the buttons).
        ImGui.SetCursorPos(new Vector2(start.X, start.Y + rowHeight + ImGui.GetStyle().ItemSpacing.Y));
        ImGui.Dummy(Vector2.Zero);

        var rowHovered = ImGui.IsMouseHoveringRect(rowMin, rowMax) && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        _hover.Row(r.Key, r.ContentId, r.Ident, rowHovered, overButton);
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
        _autoOpened = false;
        foreach (var t in _avatars.Values) t?.Dispose();
        _avatars.Clear();

        base.OnClose();
    }
}
