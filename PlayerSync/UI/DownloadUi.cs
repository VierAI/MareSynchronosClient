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
        }

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