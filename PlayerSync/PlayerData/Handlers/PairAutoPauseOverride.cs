using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using MareSynchronos.MareConfiguration;
using MareSynchronos.MareConfiguration.Models;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;

namespace MareSynchronos.PlayerData.Handlers;

/// <summary>
/// Fork: right-click entry that overrides PSync's own automatic threshold pause for one player. It is shown only when the
/// targeted player is a pair that is paused AND the stored pause reason (notes storage, PausedUids, kept across sessions) is
/// one of the automatic threshold pauses: ThresholdVram, ThresholdTriangles or ThresholdHeight. Manual, permanent and
/// syncshell/all pauses never show it. The paused player is found by the hashed content id of the targeted character, so it
/// works while the player is not synced (paused players have no cached character).
/// Clicking adds the pair's UID to the matching per-player override list in the performance config (UIDsToIgnore for
/// VRAM/triangles, UIDsToIgnoreForHeightPausing for height), which is saved to disk and survives restarts, then unpauses.
/// Only the UID is stored, nothing else about the player.
/// </summary>
internal static class PairAutoPauseOverride
{
    public static bool TryGetAutoPauseReason(Pair? pair, ServerConfigurationManager serverConfig, out PauseReason reason)
    {
        reason = PauseReason.None;
        if (pair == null || !pair.IsPaused) return false;
        reason = serverConfig.GetPauseReasonForUid(pair.UserData.UID);
        return reason is PauseReason.ThresholdVram or PauseReason.ThresholdTriangles or PauseReason.ThresholdHeight;
    }

    public static Pair? FindPausedPair(nint target, PairManager pairManager, DalamudUtilService dalamudUtil)
    {
        if (target == nint.Zero) return null;
        var visible = pairManager.GetVisiblePairs().FirstOrDefault(p => p.Address == target);
        if (visible != null) return visible;
        string ident;
        try { ident = dalamudUtil.GetHashedCIDFromPlayerPointer(target); }
        catch { return null; }
        if (string.IsNullOrEmpty(ident)) return null;
        return pairManager.TryGetPairByIdent(ident, out var pair) ? pair : null;
    }

    public static void AddMenuItem(IMenuOpenedArgs args, Pair pair, PauseReason reason, PlayerPerformanceConfigService perf, MareMediator mediator)
    {
        var height = reason == PauseReason.ThresholdHeight;
        var what = reason switch
        {
            PauseReason.ThresholdVram => "VRAM",
            PauseReason.ThresholdTriangles => "triangles",
            _ => "height",
        };
        var uid = pair.UserData.UID;
        args.AddMenuItem(new MenuItem()
        {
            Name = new SeStringBuilder().AddText($"Override auto pause ({what})").Build(),
            OnClicked = _ =>
            {
                var list = height ? perf.Current.UIDsToIgnoreForHeightPausing : perf.Current.UIDsToIgnore;
                if (!list.Contains(uid, StringComparer.Ordinal))
                {
                    list.Add(uid);
                    perf.Save();
                }
                mediator.Publish(new UnPauseMessage(pair.UserData, false));
            },
            UseDefaultPrefix = false,
            PrefixChar = 'P',
            PrefixColor = 530,
        });
    }
}