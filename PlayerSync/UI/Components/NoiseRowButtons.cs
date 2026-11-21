using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using MareSynchronos.Services;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.MareConfiguration.Models;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Mediator;
using MareSynchronos.WebAPI;

namespace MareSynchronos.UI.Components;

/// <summary>
/// Mute/unmute and pause buttons of a Noises row, shared by the standalone Noises window and the Noises folder
/// in the main window so both behave the same.
/// </summary>
internal static class NoiseRowButtons
{
    public readonly record struct Result(bool MuteClicked, bool PauseClicked, bool Hovered);

    public static float MuteWidth(UiSharedService ui)
        => Math.Max(ui.GetIconButtonSize(FontAwesomeIcon.VolumeUp).X, ui.GetIconButtonSize(FontAwesomeIcon.VolumeMute).X);

    public static float PauseWidth(UiSharedService ui) => ui.GetIconButtonSize(FontAwesomeIcon.Pause).X;

    /// <summary>Width of the row buttons (1.15.5.1210: only pause; mute moved to the speaker icon).</summary>
    public static float Width(UiSharedService ui) => PauseWidth(ui);

    /// <summary>Mute/unmute toggle (same as the old mute button): a pair permission stored on the server, so it stays when the
    /// player unloads and comes back. Muting marks the permission sticky (preferred), like the permission window does.
    /// 1.15.6.205: <paramref name="auto"/> false = a click by you: the pair leaves the blacklist auto-mute tracking, so an
    /// auto-unmute never undoes or overrides what you chose by hand.</summary>
    public static void ToggleMute(Pair pair, ApiController api, bool auto = false)
    {
        var perm = pair.UserPair!.OwnPermissions;
        var muted = perm.IsDisableSounds();
        // 1.15.6.207: before the server echo, whose redirect refresh (PairManager) then drops this pair's blacklist silencing.
        if (!auto) NoiseAutoMute.OnManualChange(pair.UserData.UID, unmuting: muted);
        if (!muted) perm.SetSticky(true);
        perm.SetDisableSounds(!muted);
        // 1.15.6.209: an automatic send is marked so its echo is not taken for a manual unmute (NoiseAutoMute.OnUnmuteConfirmed).
        if (auto) { using (NoiseAutoMute.AutoSendScope()) _ = api.UserSetPairPermissions(new(pair.UserData, perm)); }
        else _ = api.UserSetPairPermissions(new(pair.UserData, perm));
    }

    /// <summary>
    /// 1.15.5.1210: the status speaker is the mute toggle. Green/fading speaker = sound on (click to mute), red = muted
    /// (click to unmute). Hand cursor and a tooltip on hover.
    /// </summary>
    public static Result Speaker(Pair pair, TimeSpan since, UiSharedService ui, ApiController api)
    {
        var muted = pair.UserPair!.OwnPermissions.IsDisableSounds();
        if (muted) ui.IconText(FontAwesomeIcon.VolumeMute, ImGuiColors.DalamudRed);
        else ui.IconText(SoundActivity.SpeakerIcon(pair), SoundActivity.IconColor(pair, since));
        var hovered = ImGui.IsItemHovered();
        var clicked = false;
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var desc = (muted ? "Muted: you disabled sound sync with this player." : SoundActivity.Describe(pair, since))
                + "\nState: " + SoundActivity.StateLabel(pair, DateTimeOffset.UtcNow);
            ImGui.SetTooltip((muted ? "Click to unmute" : "Click to mute") + "\n" + desc);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                ToggleMute(pair, api);
                clicked = true;
            }
        }
        return new(clicked, false, hovered);
    }

    /// <summary>Empty space the size of both buttons (same row height and width), for rows that hide them.</summary>
    public static Result Placeholder(float endX, UiSharedService ui)
    {
        var width = Width(ui);
        ImGui.SameLine(endX - width);
        ImGui.Dummy(new System.Numerics.Vector2(width, ImGui.GetFrameHeight()));
        return default;
    }

    /// <summary>Draws the pause button right-aligned so it ends at <paramref name="endX"/> (window-local X).</summary>
    public static Result Draw(Pair pair, float endX, UiSharedService ui, ApiController api, MareMediator mediator)
    {
        var perm = pair.UserPair!.OwnPermissions;
        var muted = perm.IsDisableSounds();
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var pauseWidth = PauseWidth(ui);
        var hovered = false;
        var muteClicked = false;
        var pauseClicked = false;

        ImGui.SameLine(endX - pauseWidth);
        if (ui.IconButton(FontAwesomeIcon.Pause))
        {
            mediator.Publish(new PauseMessage(pair.UserData, PauseReason.Manual, PauseDuration.Indefinitely));
            pauseClicked = true;
        }
        hovered |= ImGui.IsItemHovered();
        UiSharedService.AttachToolTip($"Pause pairing with {pair.UserData.AliasOrUID}.");

        return new(muteClicked, pauseClicked, hovered);
    }
}