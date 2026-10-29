using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using MareSynchronos.Utils;
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
/// The override list stores only the UID. 1.15.5.1411: a temporary auto-pause also keeps UID, reason, hashed content id,
/// character name and expiry in vierai-config.json (TempPauses) until it expires or is lifted.
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

    /// <summary>1.15.5.1408: the pair behind the right-clicked object: by address, hashed content id from the object, or the
    /// menu's content id (works when the object is not the current target and when the pair has no cached player).</summary>
    public static Pair? ResolveMenuPair(MenuTargetDefault menuTarget, PairManager pairManager, DalamudUtilService dalamudUtil)
    {
        var addr = menuTarget.TargetObject?.Address ?? nint.Zero;
        var pair = FindPausedPair(addr, pairManager, dalamudUtil);
        if (pair != null) return pair;
        // 1.15.5.1410: a paused pair is marked offline, so its live ident (HashedCID) is empty and it is not in the ident map.
        // Fall back to the last known hashed content id (from the clicked object or the menu content id), then the last known
        // character name, across ALL pairs including paused/offline ones.
        var hashes = new List<string>(2);
        if (addr != nint.Zero)
        {
            try { var h = dalamudUtil.GetHashedCIDFromPlayerPointer(addr); if (!string.IsNullOrEmpty(h)) hashes.Add(h); } catch { }
        }
        if (menuTarget.TargetContentId != 0)
        {
            try { hashes.Add(menuTarget.TargetContentId.ToString().GetHash256()); } catch { }
        }
        List<Pair> all;
        try { all = pairManager.GetAllPairs(); } catch { return null; }
        foreach (var h in hashes)
        {
            var byCid = all.FirstOrDefault(p => string.Equals(p.HashedCID, h, StringComparison.Ordinal))
                ?? all.FirstOrDefault(p => string.Equals(p.LastKnownHashedCID, h, StringComparison.Ordinal));
            if (byCid != null) return byCid;
        }
        var name = menuTarget.TargetName;
        if (string.IsNullOrEmpty(name)) name = menuTarget.TargetObject?.Name.TextValue ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            var cidUid = MareSynchronos.Services.VierAITempPauses.FindUid(hashes, null);
            return cidUid == null ? null : pairManager.GetPairByUID(cidUid);
        }
        var byName = all.Where(p => p.IsPaused && string.Equals(p.LastKnownPlayerName, name, StringComparison.Ordinal)).ToList();
        if (byName.Count == 1) return byName[0];
        // 1.15.5.1411: after a restart the identity comes from the saved TempPauses (vierai-config.json) until it expires.
        var savedUid = MareSynchronos.Services.VierAITempPauses.FindUid(hashes, name);
        return savedUid == null ? null : pairManager.GetPairByUID(savedUid);
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
            Name = new SeStringBuilder().AddText($"Unpause (allow {what})").Build(),
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