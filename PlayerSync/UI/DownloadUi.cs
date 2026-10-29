using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Handlers;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.WebAPI.Files;
using MareSynchronos.WebAPI.Files.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Numerics;

namespace MareSynchronos.UI;

public class DownloadUi : WindowMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly ConcurrentDictionary<DownloadBatchInfo, ConcurrentDictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileUploadManager _fileTransferManager;
    private readonly UiSharedService _uiShared;
    private readonly DownloadPriorityQueue _priorityQueue;
    private readonly ConcurrentDictionary<GameObjectHandler, bool> _uploadingPlayers = new();

    // 1.15.5.1404: live download throughput at the top of the downloads window. Log scale (1 KiB/s to 1 GiB/s), 0.5 s buckets,
    // 30 s rolling history. Bytes are counted from the change in each file group's TransferredBytes between frames.
    private const int SpeedBucketMs = 500;
    private const int SpeedBuckets = 60;
    private const int SpeedCurrentBuckets = 4; // "now" = average of the last 2 s
    private const float SpeedGraphHeight = 30f;
    private const double SpeedLogMin = 10; // log2(1 KiB/s)
    private const double SpeedLogMax = 30; // log2(1 GiB/s)
    private readonly double[] _speedHistory = new double[SpeedBuckets];
    private readonly Dictionary<FileDownloadStatus, long> _speedLastSeen = new();
    private readonly HashSet<FileDownloadStatus> _speedSeenNow = new();
    private int _speedHead;
    private long _speedBucketStartMs;
    private long _speedBucketBytes;
    private long _speedLastSampleMs;
    private double _speedCurrent;
    private double _speedPeak;

    public DownloadUi(ILogger<DownloadUi> logger, DalamudUtilService dalamudUtilService, MareConfigService configService,
        FileUploadManager fileTransferManager, MareMediator mediator, UiSharedService uiShared, PerformanceCollectorService performanceCollectorService,
        DownloadPriorityQueue priorityQueue)
        : base(logger, mediator, "PlayerSync Downloads", performanceCollectorService)
    {
        _dalamudUtilService = dalamudUtilService;
        _configService = configService;
        _fileTransferManager = fileTransferManager;
        _uiShared = uiShared;
        _priorityQueue = priorityQueue;

        SizeConstraints = new WindowSizeConstraints
        {
            MaximumSize = new Vector2(500, 90),
            MinimumSize = new Vector2(500, 90),
        };

        Flags |= ImGuiWindowFlags.NoMove;
        Flags |= ImGuiWindowFlags.NoBackground;
        Flags |= ImGuiWindowFlags.NoInputs;
        Flags |= ImGuiWindowFlags.NoNavFocus;
        Flags |= ImGuiWindowFlags.NoResize;
        Flags |= ImGuiWindowFlags.NoScrollbar;
        Flags |= ImGuiWindowFlags.NoTitleBar;
        Flags |= ImGuiWindowFlags.NoDecoration;
        Flags |= ImGuiWindowFlags.NoFocusOnAppearing;

        DisableWindowSounds = true;

        ForceMainWindow = true;

        IsOpen = true;

        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.BatchInfo] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.BatchInfo, out _));
        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<GposeEndMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<PlayerUploadingMessage>(this, (msg) =>
        {
            if (msg.IsUploading)
            {
                _uploadingPlayers[msg.Handler] = true;
            }
            else
            {
                _uploadingPlayers.TryRemove(msg.Handler, out _);
            }
        });
    }

    protected override void DrawInternal()
    {
        if (_configService.Current.ShowTransferWindow)
        {
            try
            {
                SampleSpeed();
                if (!_currentDownloads.IsEmpty) DrawSpeedGraph();
            }
            catch
            {
                // ignore errors thrown from UI
            }

            try
            {
                var currentUploads = _fileTransferManager.CurrentUploadList;
                if (currentUploads.Count > 0)
                {
                    var totalUploads = currentUploads.Count;

                    var doneUploads = currentUploads.Count(c => c.IsTransferred);
                    var totalUploaded = currentUploads.Sum(c => c.Transferred);
                    var totalToUpload = currentUploads.Sum(c => c.Total);

                    UiSharedService.DrawOutlinedFont($"▲", ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.SameLine();
                    var xDistance = ImGui.GetCursorPosX();
                    UiSharedService.DrawOutlinedFont($"Compressing+Uploading {doneUploads}/{totalUploads}",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.NewLine();
                    ImGui.SameLine(xDistance);
                    UiSharedService.DrawOutlinedFont(
                        $"{UiSharedService.ByteToString(totalUploaded, addSuffix: false)}/{UiSharedService.ByteToString(totalToUpload)}",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);

                    if (_currentDownloads.Any()) ImGui.Separator();
                }
            }
            catch
            {
                // ignore errors thrown from UI
            }

            try
            {
                // Only transfers that are actually running are listed; everyone else is counted in the summary line below.
                var rows = ActiveRows(out var waitingCount);
                foreach (var item in rows)
                {
                    var dlSlot = item.Value.Count(c => c.Value.DownloadStatus == DownloadStatus.WaitingForSlot);
                    var dlQueue = item.Value.Count(c => c.Value.DownloadStatus == DownloadStatus.WaitingForQueue);
                    var dlProg = item.Value.Count(c => c.Value.DownloadStatus == DownloadStatus.Downloading);
                    var dlDecomp = item.Value.Count(c => c.Value.DownloadStatus == DownloadStatus.Decompressing);
                    var totalFiles = item.Value.Sum(c => c.Value.TotalFiles);
                    var transferredFiles = item.Value.Sum(c => c.Value.TransferredFiles);
                    var totalBytes = item.Value.Sum(c => c.Value.TotalBytes);
                    var transferredBytes = item.Value.Sum(c => c.Value.TransferredBytes);

                    UiSharedService.DrawOutlinedFont($"▼", ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.SameLine();
                    var xDistance = ImGui.GetCursorPosX();
                    UiSharedService.DrawOutlinedFont(
                        $"{BatchName(item.Key)} [W:{dlSlot}/Q:{dlQueue}/P:{dlProg}/D:{dlDecomp}]",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.NewLine();
                    ImGui.SameLine(xDistance);
                    UiSharedService.DrawOutlinedFont(
                        $"{transferredFiles}/{totalFiles} ({UiSharedService.ByteToString(transferredBytes, addSuffix: false)}/{UiSharedService.ByteToString(totalBytes)})",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                }

                var summary = WaitingSummaryText(waitingCount);
                if (summary != null)
                {
                    UiSharedService.DrawOutlinedFont(summary, ImGuiColors.DalamudGrey, new Vector4(0, 0, 0, 255), 1);
                }
            }
            catch
            {
                // ignore errors thrown from UI
            }
        }

        if (_configService.Current.ShowTransferBars)
        {
            const int transparency = 100;
            const int dlBarBorder = 3;

            foreach (var transfer in _currentDownloads)
            {
                try
                {
                    if (transfer.Key.GameObject != null && transfer.Key.GameObject.GetGameObject() is IGameObject downloadObject)
                    {
                        var screenPos = _dalamudUtilService.WorldToScreen(downloadObject);
                        if (screenPos == Vector2.Zero) continue;

                        var totalBytes = transfer.Value.Sum(c => c.Value.TotalBytes);
                        var transferredBytes = transfer.Value.Sum(c => c.Value.TransferredBytes);

                        var maxDlText = $"{UiSharedService.ByteToString(totalBytes, addSuffix: false)}/{UiSharedService.ByteToString(totalBytes)}";
                        var textSize = _configService.Current.TransferBarsShowText ? ImGui.CalcTextSize(maxDlText) : new Vector2(10, 10);

                        int dlBarHeight = _configService.Current.TransferBarsHeight > ((int)textSize.Y + 5) ? _configService.Current.TransferBarsHeight : (int)textSize.Y + 5;
                        int dlBarWidth = _configService.Current.TransferBarsWidth > ((int)textSize.X + 10) ? _configService.Current.TransferBarsWidth : (int)textSize.X + 10;

                        var dlBarStart = new Vector2(screenPos.X - dlBarWidth / 2f, screenPos.Y - dlBarHeight / 2f);
                        var dlBarEnd = new Vector2(screenPos.X + dlBarWidth / 2f, screenPos.Y + dlBarHeight / 2f);
                        var drawList = ImGui.GetBackgroundDrawList();
                        drawList.AddRectFilled(
                            dlBarStart with { X = dlBarStart.X - dlBarBorder - 1, Y = dlBarStart.Y - dlBarBorder - 1 },
                            dlBarEnd with { X = dlBarEnd.X + dlBarBorder + 1, Y = dlBarEnd.Y + dlBarBorder + 1 },
                            UiSharedService.Color(0, 0, 0, transparency), 1);
                        drawList.AddRectFilled(dlBarStart with { X = dlBarStart.X - dlBarBorder, Y = dlBarStart.Y - dlBarBorder },
                            dlBarEnd with { X = dlBarEnd.X + dlBarBorder, Y = dlBarEnd.Y + dlBarBorder },
                            UiSharedService.Color(220, 220, 220, transparency), 1);
                        drawList.AddRectFilled(dlBarStart, dlBarEnd,
                            UiSharedService.Color(0, 0, 0, transparency), 1);
                        var dlProgressPercent = transferredBytes / (double)totalBytes;
                        drawList.AddRectFilled(dlBarStart,
                            dlBarEnd with { X = dlBarStart.X + (float)(dlProgressPercent * dlBarWidth) },
                            UiSharedService.Color(50, 205, 50, transparency), 1);

                        if (_configService.Current.TransferBarsShowText)
                        {
                            // Fork: once bytes have moved, keep showing progress while the next file group waits for a slot (no 'Waiting' flash between groups).
                            var isActive = transferredBytes > 0 || transfer.Value.Any(c => c.Value.DownloadStatus is DownloadStatus.Downloading or DownloadStatus.Decompressing);
                            var downloadText = isActive
                                ? $"{UiSharedService.ByteToString(transferredBytes, addSuffix: false)}/{UiSharedService.ByteToString(totalBytes)}"
                                : "Waiting";
                            // Center the text actually drawn (textSize is the widest possible text, which left-shifts 'Waiting').
                            var drawnSize = ImGui.CalcTextSize(downloadText);
                            UiSharedService.DrawOutlinedFont(drawList, downloadText,
                                screenPos with { X = screenPos.X - drawnSize.X / 2f - 1, Y = screenPos.Y - drawnSize.Y / 2f - 1 },
                                UiSharedService.Color(255, 255, 255, transparency),
                                UiSharedService.Color(0, 0, 0, transparency), 1);
                        }
                    }
                }
                catch
                {
                    // ignore errors thrown from UI
                }
            }

            if (_configService.Current.ShowUploading)
            {
                foreach (var player in _uploadingPlayers.Select(p => p.Key).ToList())
                {
                    var screenPos = _dalamudUtilService.WorldToScreen(player.GetGameObject());
                    if (screenPos == Vector2.Zero) continue;

                    try
                    {
                        using var _ = _uiShared.UidFont.Push();
                        var uploadText = "Uploading";

                        var textSize = ImGui.CalcTextSize(uploadText);

                        var drawList = ImGui.GetBackgroundDrawList();
                        UiSharedService.DrawOutlinedFont(drawList, uploadText,
                            screenPos with { X = screenPos.X - textSize.X / 2f - 1, Y = screenPos.Y - textSize.Y / 2f - 1 },
                            UiSharedService.Color(255, 255, 0, transparency),
                            UiSharedService.Color(0, 0, 0, transparency), 2);
                    }
                    catch
                    {
                        // ignore errors thrown on UI
                    }
                }
            }
        }
    }

    public override bool DrawConditions()
    {
        if (_uiShared.EditTrackerPosition) return true;
        if (!_configService.Current.ShowTransferWindow && !_configService.Current.ShowTransferBars) return false;
        if (!_currentDownloads.Any() && _fileTransferManager.CurrentUploadCount <= 0 && !_uploadingPlayers.Any()) return false;
        if (!IsOpen) return false;
        return true;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        if (_uiShared.EditTrackerPosition)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
            Flags &= ~ImGuiWindowFlags.NoBackground;
            Flags &= ~ImGuiWindowFlags.NoInputs;
            Flags &= ~ImGuiWindowFlags.NoResize;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
            Flags |= ImGuiWindowFlags.NoBackground;
            Flags |= ImGuiWindowFlags.NoInputs;
            Flags |= ImGuiWindowFlags.NoResize;
            // 1.15.5.1409: take mouse input only while the cursor is over last frame's graph (or a graph drag is running),
            // so the graph can be dragged to move the window; the rest of the window stays click-through and NoMove.
            var mouse = ImGui.GetIO().MousePos;
            var overGraph = _graphRectValid && mouse.X >= _graphMin.X && mouse.Y >= _graphMin.Y && mouse.X <= _graphMax.X && mouse.Y <= _graphMax.Y;
            if (_graphDragging || overGraph) Flags &= ~ImGuiWindowFlags.NoInputs;
        }
        _graphRectValid = false;

        // Size the window from its content: 2 lines per transfer, 2 for uploads, widest line, all scaled by the UI scale.
        var scale = ImGuiHelpers.GlobalScale;
        var width = 300f * scale;
        var lines = 0;
        var extra = 0f;
        try
        {
            if (_configService.Current.ShowTransferWindow)
            {
                var uploads = _fileTransferManager.CurrentUploadList.Count;
                if (uploads > 0)
                {
                    lines += 2;
                    width = Math.Max(width, ImGui.CalcTextSize($"Compressing+Uploading {uploads}/{uploads}").X);
                    if (_currentDownloads.Any()) extra += 8f * scale;
                }

                if (!_currentDownloads.IsEmpty) extra += (SpeedGraphHeight + 6f) * scale; // 1.15.5.1404 speed graph

                var rows = ActiveRows(out var waitingCount);
                foreach (var item in rows)
                {
                    lines += 2;
                    width = Math.Max(width, ImGui.CalcTextSize($"{BatchName(item.Key)} [W:999/Q:999/P:999/D:999]").X);
                }

                if (WaitingSummaryText(waitingCount) is { } summaryText)
                {
                    lines += 1;
                    width = Math.Max(width, ImGui.CalcTextSize(summaryText).X);
                }
            }
        }
        catch
        {
            // ignore errors thrown from UI
        }

        if (_uiShared.EditTrackerPosition) lines = Math.Max(lines, 4);
        lines = Math.Max(lines, 2);
        var height = ImGui.GetTextLineHeightWithSpacing() * (lines + 0.5f) + ImGui.GetStyle().WindowPadding.Y * 2 + extra;
        width += ImGui.GetStyle().WindowPadding.X * 2 + 4f;
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(width, height),
            MaximumSize = new Vector2(width, height),
        };
    }

    // 1.15.5.1404: counts the bytes moved since the last frame into the current 0.5 s bucket and rolls the buckets.
    private void SampleSpeed()
    {
        var now = Environment.TickCount64;
        var recent = _speedLastSampleMs != 0 && now - _speedLastSampleMs < 1000;
        _speedLastSampleMs = now;
        if (_speedBucketStartMs == 0) _speedBucketStartMs = now;

        long delta = 0;
        _speedSeenNow.Clear();
        foreach (var batch in _currentDownloads)
        {
            foreach (var kv in batch.Value)
            {
                var st = kv.Value;
                if (st == null) continue;
                var t = st.TransferredBytes;
                _speedSeenNow.Add(st);
                if (_speedLastSeen.TryGetValue(st, out var last))
                {
                    if (t > last) delta += t - last;
                }
                else if (recent && t > 0)
                {
                    delta += t; // new file group since the last frame: everything it moved so far is new
                }
                _speedLastSeen[st] = t;
            }
        }
        if (_speedLastSeen.Count != _speedSeenNow.Count)
        {
            foreach (var gone in _speedLastSeen.Keys.Where(k => !_speedSeenNow.Contains(k)).ToList()) _speedLastSeen.Remove(gone);
        }
        _speedBucketBytes += delta;

        if (now - _speedBucketStartMs > (long)SpeedBucketMs * SpeedBuckets)
        {
            // idle longer than the whole history: start over
            Array.Clear(_speedHistory);
            _speedHead = 0;
            _speedBucketBytes = recent ? _speedBucketBytes : 0;
            _speedBucketStartMs = now;
        }
        while (now - _speedBucketStartMs >= SpeedBucketMs)
        {
            _speedHistory[_speedHead] = _speedBucketBytes * 1000.0 / SpeedBucketMs;
            _speedBucketBytes = 0;
            _speedHead = (_speedHead + 1) % SpeedBuckets;
            _speedBucketStartMs += SpeedBucketMs;
        }

        double sum = 0, peak = 0;
        for (int i = 1; i <= SpeedBuckets; i++)
        {
            var v = _speedHistory[(_speedHead - i + SpeedBuckets) % SpeedBuckets];
            if (i <= SpeedCurrentBuckets) sum += v;
            if (v > peak) peak = v;
        }
        _speedCurrent = sum / SpeedCurrentBuckets;
        _speedPeak = peak;
    }

    private static float SpeedFrac(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return 0f;
        var f = (Math.Log2(bytesPerSec) - SpeedLogMin) / (SpeedLogMax - SpeedLogMin);
        return (float)Math.Clamp(f, 0, 1);
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (bytesPerSec < 1024) return bytesPerSec.ToString("0", ci) + " B/s";
        if (bytesPerSec < 1024d * 1024) return (bytesPerSec / 1024).ToString("0.0", ci) + " KiB/s";
        if (bytesPerSec < 1024d * 1024 * 1024) return (bytesPerSec / (1024d * 1024)).ToString("0.0", ci) + " MiB/s";
        return (bytesPerSec / (1024d * 1024 * 1024)).ToString("0.00", ci) + " GiB/s";
    }

    // 1.15.5.1404: the throughput graph. Oldest bucket on the left, newest on the right; faint lines at 10 KiB/s, 100 KiB/s,
    // 1 MiB/s, 10 MiB/s and 100 MiB/s.
    private void DrawSpeedGraph()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(50f * scale, ImGui.GetContentRegionAvail().X);
        var height = SpeedGraphHeight * scale;
        var drawList = ImGui.GetWindowDrawList();
        var p0 = ImGui.GetCursorScreenPos();
        var p1 = p0 + new Vector2(width, height);

        drawList.AddRectFilled(p0, p1, UiSharedService.Color(0, 0, 0, 120), 2);
        foreach (var mark in new[] { 10d * 1024, 100d * 1024, 1024d * 1024, 10d * 1024 * 1024, 100d * 1024 * 1024 })
        {
            var y = p1.Y - SpeedFrac(mark) * height;
            drawList.AddLine(new Vector2(p0.X, y), new Vector2(p1.X, y), UiSharedService.Color(255, 255, 255, 40), 1f);
        }

        var barW = width / (SpeedBuckets - 1);
        for (int i = 1; i < SpeedBuckets; i++)
        {
            var v = _speedHistory[(_speedHead + i) % SpeedBuckets]; // i = 1 is the oldest finished bucket, SpeedBuckets-1 the newest
            var frac = SpeedFrac(v);
            if (frac <= 0) continue;
            var x = p0.X + (i - 1) * barW;
            drawList.AddRectFilled(new Vector2(x, p1.Y - frac * height), new Vector2(x + Math.Max(1f, barW - 1f), p1.Y),
                UiSharedService.Color(50, 205, 50, 190), 0f);
        }

        var text = $"{FormatSpeed(_speedCurrent)} ({FormatSpeed(_speedPeak)} peak)";
        UiSharedService.DrawOutlinedFont(drawList, text, new Vector2(p0.X + 1f * scale, p0.Y + 1f),
            UiSharedService.Color(255, 255, 255, 255), UiSharedService.Color(0, 0, 0, 255), 1);

        // 1.15.5.1409: click and drag the graph to move the downloads window (no need to enable the move option).
        _graphMin = p0; _graphMax = p1; _graphRectValid = true;
        ImGui.InvisibleButton("##psyncSpeedGraphDrag", new Vector2(width, height));
        _graphDragging = ImGui.IsItemActive();
        if (_graphDragging && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            var delta = ImGui.GetIO().MouseDelta;
            if (delta.X != 0 || delta.Y != 0) ImGui.SetWindowPos(ImGui.GetWindowPos() + delta); // persisted via imgui.ini, same as the move mode
        }
        if (ImGui.IsItemHovered() || _graphDragging) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        ImGui.Dummy(new Vector2(width, 2f * scale));
    }

    private Vector2 _graphMin, _graphMax;
    private bool _graphRectValid, _graphDragging;

    // The transfers shown in the list: batches with a file downloading or decompressing right now (at most ParallelDownloads).
    // Everything else (waiting for a slot) is only counted.
    private List<KeyValuePair<DownloadBatchInfo, ConcurrentDictionary<string, FileDownloadStatus>>> ActiveRows(out int waiting)
    {
        var rows = new List<KeyValuePair<DownloadBatchInfo, ConcurrentDictionary<string, FileDownloadStatus>>>();
        waiting = 0;
        var cap = Math.Max(1, _configService.Current.ParallelDownloads);
        foreach (var item in _currentDownloads)
        {
            // Fork: a started transfer stays listed between file groups instead of dropping to '+N waiting' for a moment.
            var active = item.Value.Any(c => c.Value.DownloadStatus is DownloadStatus.Downloading or DownloadStatus.Decompressing)
                || item.Value.Sum(c => c.Value.TransferredBytes) > 0;
            if (active && rows.Count < cap) rows.Add(item);
            else waiting++;
        }

        return rows;
    }

    // "+N waiting"; null when nobody is waiting.
    private string? WaitingSummaryText(int waiting)
    {
        if (waiting <= 0) return null;
        return $"+{waiting} waiting";
    }

    // The batch's DisplayName is the character name captured when the download started. It can still be empty then
    // (the handler fills its name on a later frame), so fall back to the live handler name, then the pair.
    private static string BatchName(DownloadBatchInfo batch)
    {
        if (!string.IsNullOrWhiteSpace(batch.DisplayName)) return batch.DisplayName;
        var live = batch.GameObject?.Name;
        if (!string.IsNullOrWhiteSpace(live)) return live;
        var pairName = batch.Pair?.PlayerName;
        if (!string.IsNullOrWhiteSpace(pairName)) return pairName;
        return batch.Pair?.UserData.AliasOrUID ?? batch.DisplayType;
    }
}