using MareSynchronos.MareConfiguration.Configurations;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Mediator;
using System.Globalization;

namespace MareSynchronos.Services;

/// <summary>
/// PSync perf warning: which of a pair's metrics are over YOUR existing warning (and auto-pause) thresholds from
/// Settings > Performance. Used by the Visible-section row label and the optional one-time chat notice.
/// </summary>
public static class PSyncPerfWarning
{
    /// <summary>The compact format, in one place. {0} = VRAM in MiB, {0} = triangles (thousands with "k"). "T" = triangles
    /// (U+0394 is not used: Dalamud's default glyphs come from the game's AXIS font file and Greek is not guaranteed).</summary>
    public const string VramFormat = "{0}M";
    public const string TrisKFormat = "{0}kT";
    public const string TrisFormat = "{0}T";
    public const string Separator = " \u00B7 ";

    public readonly record struct Result(bool VramWarn, bool TrisWarn, bool VramPause, bool TrisPause)
    {
        public bool Any => VramWarn || TrisWarn;
    }

    public static Result Evaluate(Pair pair, PlayerPerformanceConfig perf)
    {
        try
        {
            if (perf.UIDsToIgnore.Exists(u => string.Equals(u, pair.UserData.UID, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(pair.UserData.Alias) && string.Equals(u, pair.UserData.Alias, StringComparison.Ordinal))))
                return default;
            var vram = pair.LastAppliedApproximateVRAMBytes;
            var tris = pair.LastAppliedDataTris;
            bool vramWarn = vram >= 0 && perf.VRAMSizeWarningThresholdMiB > 0 && vram > perf.VRAMSizeWarningThresholdMiB * 1024L * 1024L;
            bool trisWarn = tris >= 0 && perf.TrisWarningThresholdThousands > 0 && tris > perf.TrisWarningThresholdThousands * 1000L;
            bool vramPause = vram >= 0 && perf.VRAMSizeAutoPauseThresholdMiB > 0 && vram > perf.VRAMSizeAutoPauseThresholdMiB * 1024L * 1024L;
            bool trisPause = tris >= 0 && perf.TrisAutoPauseThresholdThousands > 0 && tris > perf.TrisAutoPauseThresholdThousands * 1000L;
            // Over auto-pause also counts as a warning (shown red) even if the warning limit is off.
            return new(vramWarn || vramPause, trisWarn || trisPause, vramPause, trisPause);
        }
        catch
        {
            return default;
        }
    }

    public static string VramText(long bytes) => string.Format(CultureInfo.InvariantCulture, VramFormat, bytes / (1024 * 1024));

    public static string TrisText(long tris) => tris >= 1000
        ? string.Format(CultureInfo.InvariantCulture, TrisKFormat, tris / 1000)
        : string.Format(CultureInfo.InvariantCulture, TrisFormat, tris);

    public static string Label(Pair pair, Result w)
    {
        var parts = new List<string>(2);
        if (w.VramWarn) parts.Add(VramText(pair.LastAppliedApproximateVRAMBytes));
        if (w.TrisWarn) parts.Add(TrisText(pair.LastAppliedDataTris));
        return string.Join(Separator, parts);
    }

    public static string Tooltip(Pair pair, Result w)
    {
        var lines = new List<string>(3) { "Over your performance warning limits (Settings > Performance):" };
        if (w.VramWarn) lines.Add($"Approx. VRAM {pair.LastAppliedApproximateVRAMBytes / (1024 * 1024)} MB" + (w.VramPause ? " (over auto-pause)" : string.Empty));
        if (w.TrisWarn) lines.Add($"Approx. triangles {pair.LastAppliedDataTris:N0}" + (w.TrisPause ? " (over auto-pause)" : string.Empty));
        return string.Join(Environment.NewLine, lines);
    }

    // ---- one-time chat notice (per pair per session, rate-limited) ----
    private static readonly HashSet<string> s_notified = new(StringComparer.Ordinal);
    private static DateTimeOffset s_lastChat = DateTimeOffset.MinValue;
    /// <summary>At most one chat line per this interval; pairs not yet told are told on a later tick.</summary>
    public static readonly TimeSpan ChatMinInterval = TimeSpan.FromSeconds(10);

    /// <summary>Called about once a second with the visible pairs. Prints at most one line per interval.</summary>
    public static void TickChat(IEnumerable<Pair> visible, PlayerPerformanceConfig perf, MareMediator mediator)
    {
        if (!VierAIConfigStore.Current.PerfWarningInChat) return;
        var now = DateTimeOffset.UtcNow;
        if (now - s_lastChat < ChatMinInterval) return;
        foreach (var pair in visible)
        {
            var uid = pair.UserData.UID;
            if (s_notified.Contains(uid)) continue;
            var w = Evaluate(pair, perf);
            if (!w.Any) continue;
            s_notified.Add(uid);
            s_lastChat = now;
            mediator.Publish(new PSyncChatNoticeMessage(
                $"{pair.UserData.AliasOrUID} is over your performance warning limit: {Label(pair, w)}" + (w.VramPause || w.TrisPause ? " (over auto-pause)" : string.Empty)));
            return;
        }
    }
}

/// <summary>PSync: print one line to the game chat (warning style), regardless of the notification location setting.</summary>
public record PSyncChatNoticeMessage(string Message) : MessageBase;
