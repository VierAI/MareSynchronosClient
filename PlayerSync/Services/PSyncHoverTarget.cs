using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.Models;

namespace MareSynchronos.Services;

/// <summary>
/// 1.15.5.1414: targeting from a UI hover is temporary. DalamudUtilService remembers the target that was there before
/// the hover and puts it back on unhover. Hovering rows call <see cref="Touch"/> every frame; if no row reports a hover
/// for a short time (row no longer drawn, window closed or hidden), the service restores the old target by itself,
/// so a hover can never leave a permanent focus target behind.
/// </summary>
public static class PSyncHoverTarget
{
    public const long StaleAfterMs = 300;
    private static long s_lastTouch = long.MinValue / 2;

    public static long LastTouchTick => Interlocked.Read(ref s_lastTouch);

    /// <summary>Marks a hover this frame. Returns true when the previous hover report is stale (hover was interrupted).</summary>
    public static bool Touch()
    {
        var now = Environment.TickCount64;
        var prev = Interlocked.Exchange(ref s_lastTouch, now);
        return now - prev > StaleAfterMs;
    }
}

/// <summary>Temporary hover target: Pair = null ends the hover and restores the previous target.</summary>
public record HoverTargetPairMessage(Pair? Pair, TargetType TargetType) : MessageBase;
