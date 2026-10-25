using Dalamud.Bindings.ImGui;
using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.Models;

namespace MareSynchronos.UI.Components;

/// <summary>
/// Shared hover/target behaviour for Noises rows (the Noises window and the Noises folder in the main window), 1.15.5.910.
/// - Hovering a row does the same temporary focus the main window pair rows do (when "target pairs on hover" is on).
/// - Right-clicking a row (not on one of its buttons) targets that pair, like clicking the eye icon in the main window.
/// Usage per frame: BeginFrame(), Row(...) for every drawn row, EndFrame().
/// </summary>
internal sealed class PairHoverTargeter
{
    private readonly MareMediator _mediator;
    private readonly MareConfigService _configService;
    private string? _hoveredUid;
    private bool _hoveredThisFrame;

    public PairHoverTargeter(MareMediator mediator, MareConfigService configService)
    {
        _mediator = mediator;
        _configService = configService;
    }

    public void BeginFrame() => _hoveredThisFrame = false;

    /// <param name="rowHovered">The mouse is over this row (and its window).</param>
    /// <param name="overButton">The mouse is over one of the row's buttons.</param>
    public void Row(Pair pair, bool rowHovered, bool overButton)
    {
        if (!rowHovered) return;
        _hoveredThisFrame = true;

        var cfg = _configService.Current;
        if (cfg.TargetPairsOnHover && !string.Equals(_hoveredUid, pair.UserData.UID, StringComparison.Ordinal))
        {
            _hoveredUid = pair.UserData.UID;
            _mediator.Publish(new TargetPairMessage(pair, cfg.OnHoverTargetType));
        }

        if (!overButton && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _mediator.Publish(new TargetPairMessage(pair, cfg.UseFocusTarget ? TargetType.FocusTarget : TargetType.Target));
        }
    }

    public void EndFrame()
    {
        if (_hoveredThisFrame || _hoveredUid == null) return;
        _hoveredUid = null;
        _mediator.Publish(new TargetPairMessage(null, _configService.Current.OnHoverTargetType));
    }
}