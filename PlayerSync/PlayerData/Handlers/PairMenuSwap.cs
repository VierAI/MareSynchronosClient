using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using MareSynchronos.MareConfiguration;
using MareSynchronos.MareConfiguration.Models;
using MareSynchronos.PlayerData.Pairs;

namespace MareSynchronos.PlayerData.Handlers;

/// <summary>
/// Fork (VierAI 2026-10-23): in the in-game right-click menu on a visible synced player who is not a two-way direct pair,
/// "Reapply Last Data" takes the top-level spot that "Send Pair Request" had, and "Send Pair Request" moves into the
/// "Pair Data" submenu where "Reapply Last Data" was. Only when that submenu is actually shown for the target (pair has
/// data, not paused, Pair Data in the menu order); otherwise both menus behave as before (e.g. strangers still get
/// "Send Pair Request" at the top level, two-way pairs keep "Reapply Last Data" in the submenu).
/// </summary>
internal static class PairMenuSwap
{
    /// <summary>True when the Pair Data submenu will be shown for this pair in the world (non-chat) menu.</summary>
    public static bool Applies(Pair? pair, MareConfigService config)
    {
        if (pair == null || !pair.HasCachedPlayer || pair.IsPaused) return false;
        return Array.IndexOf(config.Current.ContextMenuOrder, ContextMenuItemId.PairData) >= 0;
    }

    /// <summary>Top-level item put where "Send Pair Request" used to be. Skipped if Reapply is already a top-level item.</summary>
    public static void AddTopLevelReapply(IMenuOpenedArgs args, Pair pair, MareConfigService config)
    {
        if (Array.IndexOf(config.Current.ContextMenuOrder, ContextMenuItemId.ReapplyLastData) >= 0) return;
        args.AddMenuItem(new MenuItem()
        {
            Name = new SeStringBuilder().AddText("Reapply Last Data").Build(),
            OnClicked = _ => pair.ReapplyCachedDataByUser(),
            UseDefaultPrefix = false,
            PrefixChar = 'P',
            PrefixColor = 530,
        });
    }

    public static MenuItem SendPairRequestItem(Action send) => new()
    {
        Name = new SeStringBuilder().AddText("Send Pair Request").Build(),
        OnClicked = _ => send(),
    };
}