using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Mediator;

namespace MareSynchronos.PlayerData.Handlers;

/// <summary>
/// "Prioritize" / "Remove Priority" entry for the in-game "PlayerSync Pair Data" right-click submenu (1.15.5.910).
/// Adds or removes the pair's UID in the download priority list (tier 0) and re-tiers waiting downloads right away,
/// the same way the Priority tab does.
/// </summary>
internal static class PairMenuPrioritize
{
    public static void AddItem(List<MenuItem> menuItems, Pair pair, PlayerPerformanceConfigService config, MareMediator mediator)
    {
        var uid = pair.UserData.UID;
        var list = config.Current.UIDsToPrioritize;
        var listed = list.Contains(uid, StringComparer.Ordinal);

        menuItems.Add(new MenuItem()
        {
            Name = new SeStringBuilder().AddText(listed ? "Remove Priority" : "Prioritize").Build(),
            OnClicked = _ =>
            {
                var current = config.Current.UIDsToPrioritize;
                if (listed) current.RemoveAll(p => string.Equals(p, uid, StringComparison.Ordinal));
                else if (!current.Contains(uid, StringComparer.Ordinal)) current.Add(uid);
                config.Save();
                mediator.Publish(new DownloadLimitChangedMessage());
                mediator.Publish(new PriorityChangedMessage());
            }
        });
    }
}