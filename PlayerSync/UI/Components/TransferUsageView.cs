using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MareSynchronos.Services.Usage;
using System.Numerics;

namespace MareSynchronos.UI.Components;

/// <summary>Fork (1.15.5.1211): the transfer usage view (line graph of MiB per minute, bar graph of monthly totals, download and upload, and past sessions). Shared by the Usage window and
/// Settings > Transfers > Usage. Arrays and lists are reused; they are refreshed at most once a second.</summary>
public sealed class TransferUsageView
{
    private static readonly Vector4 DownColor = new(0.35f, 0.65f, 1f, 1f);
    private static readonly Vector4 UpColor = new(1f, 0.6f, 0.2f, 1f);
    private readonly float[] _down = new float[TransferUsage.Minutes];
    private readonly float[] _up = new float[TransferUsage.Minutes];
    private readonly List<UsageSession> _sessions = new();
    private const int MonthCount = 12;
    private readonly long[] _monthDown = new long[MonthCount];
    private readonly long[] _monthUp = new long[MonthCount];
    private readonly DateTime[] _monthStart = new DateTime[MonthCount];
    private readonly string[] _monthLabel = new string[MonthCount];
    private readonly string[] _monthTip = new string[MonthCount];
    private long _monthMax = 1;
    private int _count;
    private float _max = 1f;
    private long _seenVersion = -1;
    private long _nextRefresh;
    private string _header = string.Empty;
    private string _overlay = string.Empty;
    private string _minutesText = string.Empty;
    private readonly List<(string Start, string Length, string Down, string Up)> _sessionRows = new();

    private void Refresh()
    {
        var now = Environment.TickCount64;
        if (now < _nextRefresh) return;
        _nextRefresh = now + 1000;
        _seenVersion = TransferUsage.Version;
        _count = TransferUsage.CopyMinutes(_down, _up);
        _max = 0.01f;
        for (var i = 0; i < _count; i++) _max = Math.Max(_max, Math.Max(_down[i], _up[i]));
        TransferUsage.CopySessions(_sessions);
        var length = DateTimeOffset.UtcNow - TransferUsage.SessionStart;
        _header = $"This session ({FormatDuration(length.TotalSeconds)}): downloaded {UiSharedService.ByteToString(TransferUsage.SessionDownloaded)}, uploaded {UiSharedService.ByteToString(TransferUsage.SessionUploaded)}";
        _overlay = $"MiB per minute (max {_max:0.##})";
        _minutesText = $"Last {_count} minute(s), oldest on the left.";
        TransferUsage.CopyMonths(_monthDown, _monthUp, _monthStart, MonthCount);
        _monthMax = 1;
        for (var i = 0; i < MonthCount; i++)
        {
            _monthMax = Math.Max(_monthMax, Math.Max(_monthDown[i], _monthUp[i]));
            _monthLabel[i] = _monthStart[i].ToString("MMM", System.Globalization.CultureInfo.InvariantCulture);
            _monthTip[i] = $"{_monthStart[i]:MMMM yyyy}{(i == MonthCount - 1 ? " (so far)" : string.Empty)}\nDownloaded: {UiSharedService.ByteToString(_monthDown[i])} ({_monthDown[i]:N0} bytes)\nUploaded: {UiSharedService.ByteToString(_monthUp[i])} ({_monthUp[i]:N0} bytes)";
        }
        _sessionRows.Clear();
        for (var i = 0; i < _sessions.Count; i++)
        {
            var s = _sessions[i];
            _sessionRows.Add((s.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + (i == 0 ? " (now)" : string.Empty),
                FormatDuration(s.DurationSeconds), UiSharedService.ByteToString(s.Downloaded), UiSharedService.ByteToString(s.Uploaded)));
        }
    }

    public void Draw(UiSharedService ui)
    {
        Refresh();
        ImGui.TextUnformatted(_header);
        UiSharedService.AttachToolTip("PlayerSync file transfers only (compressed bytes as sent over the network). The server connection itself is not counted.");

        var width = Math.Max(200f, ImGui.GetContentRegionAvail().X);
        var size = new Vector2(width, 120 * ImGuiHelpers.GlobalScale);
        if (_count > 1)
        {
            // Two lines on one graph: download (blue), then upload (orange) drawn over it with a transparent frame.
            var max = _max * 1.1f;
            var pos = ImGui.GetCursorPos();
            using (ImRaii.PushColor(ImGuiCol.PlotLines, DownColor))
                ImGui.PlotLines("##usageDown", _down.AsSpan(0, _count), 0, _overlay, 0f, max, size);
            var after = ImGui.GetCursorPos();
            ImGui.SetCursorPos(pos);
            using (ImRaii.PushColor(ImGuiCol.PlotLines, UpColor).Push(ImGuiCol.FrameBg, Vector4.Zero))
                ImGui.PlotLines("##usageUp", _up.AsSpan(0, _count), 0, string.Empty, 0f, max, size);
            ImGui.SetCursorPos(after);
        }
        else ImGui.TextDisabled("The graph starts after the first full minute.");
        ImGui.TextColored(DownColor, "Download");
        ImGui.SameLine();
        ImGui.TextColored(UpColor, "Upload");
        ImGui.SameLine();
        ImGui.TextDisabled(_minutesText);
        UiSharedService.AttachToolTip("Each transfer is added to the minute it finished or was cancelled, with the bytes it actually moved.");

        ImGuiHelpers.ScaledDummy(4);
        DrawMonths(width);

        ImGuiHelpers.ScaledDummy(4);
        ImGui.TextUnformatted("Sessions (newest first, last 100 kept)");
        using (ImRaii.Child("##usagesessions", new Vector2(0, 160 * ImGuiHelpers.GlobalScale), true))
        {
            if (ImGui.BeginTable("##usagesess", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Started");
                ImGui.TableSetupColumn("Length");
                ImGui.TableSetupColumn("Downloaded");
                ImGui.TableSetupColumn("Uploaded");
                ImGui.TableHeadersRow();
                foreach (var row in _sessionRows)
                {
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Start);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Length);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Down);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Up);
                }
                ImGui.EndTable();
            }
        }

        if (ui.IconTextButton(FontAwesomeIcon.Trash, "Reset usage history") && ImGui.GetIO().KeyCtrl)
        {
            TransferUsage.Reset();
            _nextRefresh = 0;
        }
        UiSharedService.AttachToolTip("Hold CTRL and click. Clears all past sessions and monthly totals and starts this session from zero.");
    }

    /// <summary>Bar graph of the last 12 monthly totals: a download bar and an upload bar per month, hover for exact values.</summary>
    private void DrawMonths(float width)
    {
        ImGui.TextUnformatted("Monthly totals (last 12 months)");
        UiSharedService.AttachToolTip("Same counting as the graph above: each transfer is added to the month (local time) it finished or was cancelled. Months from before this view existed are seeded from the session history.");
        var scale = ImGuiHelpers.GlobalScale;
        var height = 120 * scale;
        var labelH = ImGui.GetTextLineHeight();
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height + labelH + 4 * scale);
        ImGui.InvisibleButton("##usagemonths", size);
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();
        var dl = ImGui.GetWindowDrawList();
        var plotMax = origin + new Vector2(width, height);
        dl.AddRectFilled(origin, plotMax, ImGui.GetColorU32(ImGuiCol.FrameBg), ImGui.GetStyle().FrameRounding);
        var max = _monthMax * 1.1;
        var slot = width / MonthCount;
        var pad = Math.Max(2f, slot * 0.15f);
        var barW = (slot - 2 * pad) / 2f;
        var downCol = ImGui.GetColorU32(DownColor);
        var upCol = ImGui.GetColorU32(UpColor);
        var textCol = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var hoverIdx = -1;
        for (var i = 0; i < MonthCount; i++)
        {
            var x0 = origin.X + i * slot;
            if (hovered && mouse.X >= x0 && mouse.X < x0 + slot) { hoverIdx = i; dl.AddRectFilled(new Vector2(x0, origin.Y), new Vector2(x0 + slot, plotMax.Y), ImGui.GetColorU32(ImGuiCol.FrameBgHovered)); }
            var dh = (float)(_monthDown[i] / max) * (height - 2 * scale);
            var uh = (float)(_monthUp[i] / max) * (height - 2 * scale);
            if (_monthDown[i] > 0) dl.AddRectFilled(new Vector2(x0 + pad, plotMax.Y - Math.Max(1f, dh)), new Vector2(x0 + pad + barW, plotMax.Y), downCol);
            if (_monthUp[i] > 0) dl.AddRectFilled(new Vector2(x0 + pad + barW, plotMax.Y - Math.Max(1f, uh)), new Vector2(x0 + pad + 2 * barW, plotMax.Y), upCol);
            var ts = ImGui.CalcTextSize(_monthLabel[i]);
            dl.AddText(new Vector2(x0 + (slot - ts.X) / 2f, plotMax.Y + 2 * scale), textCol, _monthLabel[i]);
        }
        dl.AddText(origin + new Vector2(4 * scale, 2 * scale), textCol, $"max {UiSharedService.ByteToString(_monthMax)}");
        if (hoverIdx >= 0) ImGui.SetTooltip(_monthTip[hoverIdx]);
    }

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }
}
