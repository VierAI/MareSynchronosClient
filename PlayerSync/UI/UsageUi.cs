using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.UI.Components;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace MareSynchronos.UI;

/// <summary>Fork (1.15.5.1211): "PlayerSync Usage" window, opened from the main window title bar (chart icon).</summary>
internal sealed class UsageUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiShared;
    private readonly TransferUsageView _view = new();

    public UsageUi(ILogger<UsageUi> logger, MareMediator mediator, PerformanceCollectorService performanceCollector, UiSharedService uiShared)
        : base(logger, mediator, "PlayerSync Usage###PlayerSyncUsage", performanceCollector)
    {
        _uiShared = uiShared;
        SizeConstraints = new() { MinimumSize = new Vector2(420, 380), MaximumSize = new Vector2(1600, 2000) };
    }

    protected override void DrawInternal() => _view.Draw(_uiShared);
}
