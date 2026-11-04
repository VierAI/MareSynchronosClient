using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Utils;
using System.Globalization;
using System.Numerics;

namespace MareSynchronos.UI;

/// <summary>
/// Fork (1.15.5.1424): Help > Their Pair Data > Workload. How many players are synced and how heavy they are, from the
/// values PSync caches per apply (same PSyncWorkload sums as the debug IPC). Refreshes once a second; no file parsing.
/// </summary>
public partial class HelpUi
{
    private static readonly TimeSpan WorkloadRefresh = TimeSpan.FromSeconds(1);
    private DateTimeOffset _workloadLast = DateTimeOffset.MinValue;
    private PSyncWorkload.Totals? _workload;
    private int _workloadOnline;
    private List<Pair> _workloadRows = [];
    private int _workloadSortColumn = 1; // 0 name, 1 VRAM, 2 tris, 3 size
    private bool _workloadSortAscending;
    private int _workloadTop = 25;

    private void DrawPairWorkload()
    {
        _uiShared.BigText("Synced Player Workload");
        ImGuiHelpers.ScaledDummy(2);

        var now = DateTimeOffset.UtcNow;
        if (_workload == null || now - _workloadLast >= WorkloadRefresh)
        {
            _workloadLast = now;
            var visible = _pairManager.GetVisiblePairs();
            _workload = PSyncWorkload.Compute(visible);
            _workloadOnline = _pairManager.GetOnlineUserPairs().Count;
            _workloadRows = visible.Where(PSyncWorkload.IsLoaded).ToList();
            SortWorkloadRows();
        }
        var w = _workload;

        ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture,
            $"Online pairs: {_workloadOnline}   Visible: {w.Visible}   Loaded: {w.Loaded}   Waiting: {w.Waiting}   Applying: {w.Applying}   Paused: {w.Paused}"));
        ImGuiHelpers.ScaledDummy(2);
        DrawWorkloadTotal("Total file size", w.Bytes.ToByteString(), w.MaxPairBytes.ToByteString(), w.BytesNotComputed);
        DrawWorkloadTotal("VRAM estimate", w.VramBytes.ToByteString(), w.MaxPairVramBytes.ToByteString(), w.VramNotComputed);
        DrawWorkloadTotal("Triangles", FormatTris(w.Tris), FormatTris(w.MaxPairTris), w.TrisNotComputed);
        _uiShared.DrawHelpText("Sums over loaded pairs, using the values PlayerSync worked out when each pair was last applied. "
            + "VRAM is the texture size estimate used for the VRAM warning (after the texture cap), not measured GPU memory. "
            + "Pairs whose value is not computed yet are left out and counted separately.");

        ImGuiHelpers.ScaledDummy(5);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ImGui.SliderInt("Show top", ref _workloadTop, 5, 100);

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.SizingFixedFit;
        var size = new Vector2(0, Math.Max(100f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y - 10f * ImGuiHelpers.GlobalScale));
        using var table = ImRaii.Table("##workloadTable", 4, flags, size);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("VRAM", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Triangles", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("File size", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableHeadersRow();

        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsDirty)
        {
            _workloadSortColumn = specs.Specs.ColumnIndex;
            _workloadSortAscending = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
            SortWorkloadRows();
            specs.SpecsDirty = false;
        }

        foreach (var pair in _workloadRows.Take(_workloadTop))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(pair.PlayerName ?? pair.UserData.AliasOrUID);
            ImGui.TableNextColumn(); DrawWorkloadCell(pair.LastAppliedApproximateVRAMBytes, v => v.ToByteString());
            ImGui.TableNextColumn(); DrawWorkloadCell(pair.LastAppliedDataTris, FormatTris);
            ImGui.TableNextColumn(); DrawWorkloadCell(pair.LastAppliedDataBytes, v => v.ToByteString());
        }
    }

    private static void DrawWorkloadTotal(string label, string total, string max, int notComputed)
    {
        ImGui.TextUnformatted($"{label}: {total}   (largest pair {max})");
        if (notComputed > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"{notComputed} not computed yet");
        }
    }

    private static void DrawWorkloadCell(long value, Func<long, string> format)
    {
        if (value < 0) ImGui.TextColored(ImGuiColors.DalamudGrey, "not computed");
        else ImGui.TextUnformatted(format(value));
    }

    private static string FormatTris(long tris)
        => tris >= 1_000_000 ? string.Create(CultureInfo.InvariantCulture, $"{tris / 1_000_000.0:0.00}M")
         : tris >= 1_000 ? string.Create(CultureInfo.InvariantCulture, $"{tris / 1_000.0:0.0}k")
         : tris.ToString(CultureInfo.InvariantCulture);

    private void SortWorkloadRows()
    {
        Func<Pair, long> key = _workloadSortColumn switch
        {
            2 => p => p.LastAppliedDataTris,
            3 => p => p.LastAppliedDataBytes,
            _ => p => p.LastAppliedApproximateVRAMBytes,
        };
        if (_workloadSortColumn == 0)
        {
            _workloadRows.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.PlayerName ?? string.Empty, b.PlayerName ?? string.Empty) * (_workloadSortAscending ? 1 : -1));
            return;
        }
        _workloadRows.Sort((a, b) => key(a).CompareTo(key(b)) * (_workloadSortAscending ? 1 : -1));
    }
}
