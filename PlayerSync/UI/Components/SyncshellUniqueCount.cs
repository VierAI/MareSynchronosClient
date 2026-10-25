using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;

namespace MareSynchronos.UI.Components;

/// <summary>
/// Fork: "unique" members per syncshell = people you'd lose by leaving it (not directly paired with you and in no other
/// syncshell you're in; you are never in your own pair list). Computed in one pass over PairManager.PairsWithGroups and
/// cached until PairManager rebuilds that dictionary (it does so on every pair/group membership change via RecreateLazy),
/// so the per-frame cost is one reference compare plus a dictionary lookup.
/// </summary>
public static class SyncshellUniqueCount
{
    private static readonly object Lock = new();
    private static object? _source;
    private static Dictionary<string, int> _unique = new(StringComparer.Ordinal);
    private static int _total;

    public static bool Enabled => VierAIConfigStore.Current.ShowSyncshellUniqueCount;

    private static void Refresh(PairManager pairManager)
    {
        var pairsWithGroups = pairManager.PairsWithGroups;
        lock (Lock)
        {
            if (ReferenceEquals(pairsWithGroups, _source)) return;
            var unique = new Dictionary<string, int>(StringComparer.Ordinal);
            var total = 0;
            foreach (var (pair, groups) in pairsWithGroups)
            {
                if (groups.Count != 1) continue;
                if (pair.IndividualPairStatus != API.Data.Enum.IndividualPairStatus.None) continue;
                var gid = groups[0].GID;
                unique[gid] = unique.GetValueOrDefault(gid) + 1;
                total++;
            }
            _unique = unique;
            _total = total;
            _source = pairsWithGroups;
        }
    }

    public static int Get(PairManager pairManager, string gid)
    {
        Refresh(pairManager);
        return _unique.GetValueOrDefault(gid);
    }

    /// <summary>Total from the last refresh (refreshed whenever a syncshell row is drawn).</summary>
    public static int LastTotal => _total;

    public static int GetTotal(PairManager pairManager)
    {
        Refresh(pairManager);
        return _total;
    }

    /// <summary>Draws "(N)" in green on the current line, with an explanatory tooltip.</summary>
    public static void Draw(PairManager pairManager, string gid, int totalMembers)
    {
        if (!Enabled) return;
        var unique = Get(pairManager, gid);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ImGuiColors.HealerGreen, $"({unique})");
        UiSharedService.AttachToolTip(TooltipLine(unique, totalMembers));
    }

    public static string TooltipLine(int unique, int totalMembers) =>
        $"{totalMembers} members, ({unique}) only here: leaving this shell would drop {unique} {(unique == 1 ? "person" : "people")} you aren't connected to any other way" + Environment.NewLine
        + "(not directly paired with you and not in any of your other syncshells)";
}
