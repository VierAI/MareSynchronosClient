using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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

    /// <summary>Total width of both buttons plus the spacing between them.</summary>
    public static float Width(UiSharedService ui) => MuteWidth(ui) + ImGui.GetStyle().ItemSpacing.X + PauseWidth(ui);

    /// <summary>Empty space the size of both buttons (same row height and width), for rows that hide them.</summary>
    public static Result Placeholder(float endX, UiSharedService ui)
    {
        var width = Width(ui);
        ImGui.SameLine(endX - width);
        ImGui.Dummy(new System.Numerics.Vector2(width, ImGui.GetFrameHeight()));
        return default;
    }

    /// <summary>Draws both buttons right-aligned so the pause button ends at <paramref name="endX"/> (window-local X).</summary>
    public static Result Draw(Pair pair, float endX, UiSharedService ui, ApiController api, MareMediator mediator)
    {
        var perm = pair.UserPair!.OwnPermissions;
        var muted = perm.IsDisableSounds();
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var pauseWidth = PauseWidth(ui);
        var hovered = false;
        var muteClicked = false;
        var pauseClicked = false;

        ImGui.SameLine(endX - pauseWidth - spacingX - MuteWidth(ui));
        if (ui.IconButton(muted ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute))
        {
            // Mute is a pair permission (stored on the server per user), so it stays when the player unloads and comes back.
            if (!muted) perm.SetSticky(true);
            perm.SetDisableSounds(!muted);
            _ = api.UserSetPairPermissions(new(pair.UserData, perm));
            muteClicked = true;
        }
        hovered |= ImGui.IsItemHovered();
        UiSharedService.AttachToolTip(muted ? $"Enable sound sync with {pair.UserData.AliasOrUID}." : $"Disable sound sync with {pair.UserData.AliasOrUID}.");

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