using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using MareSynchronos.MareConfiguration;
using MareSynchronos.MareConfiguration.Models;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Mediator;
using MareSynchronos.UI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotificationType = MareSynchronos.MareConfiguration.Models.NotificationType;

namespace MareSynchronos.Services;

public class NotificationService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly INotificationManager _notificationManager;
    private readonly IChatGui _chatGui;
    private readonly MareConfigService _configurationService;
    private readonly StageConfigService _stageConfigService;
    DalamudLinkPayload? _inviteTogglePayload = null;
    DalamudLinkPayload? _stageUpdatePayload = null;

    string _savedStageFilename = "";
    string _savedStageId = "";

    public NotificationService(ILogger<NotificationService> logger, MareMediator mediator,
        DalamudUtilService dalamudUtilService,
        INotificationManager notificationManager,
        IChatGui chatGui, MareConfigService configurationService, StageConfigService stageConfigService) : base(logger, mediator)
    {
        _dalamudUtilService = dalamudUtilService;
        _notificationManager = notificationManager;
        _chatGui = chatGui;
        _configurationService = configurationService;
        _stageConfigService = stageConfigService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Mediator.Subscribe<NotificationMessage>(this, ShowNotification);
        Mediator.Subscribe<PSyncChatNoticeMessage>(this, m => PrintWarnChat(m.Message, m.Pair));

        _inviteTogglePayload = _chatGui.AddChatLinkHandler(0, (_, _) => Mediator.Publish(new PSyncOpenPairRequestsMessage())); // 1702: open, not toggle
        // LifeStreamHandler uses ID 1
        _stageUpdatePayload = _chatGui.AddChatLinkHandler(2, (_, _) => Mediator.Publish(new OpenStageUpdateWindow(_savedStageId, _savedStageFilename)));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _chatGui.RemoveChatLinkHandler();
        _inviteTogglePayload = null;
        UnsubscribeAll();
        return Task.CompletedTask;
    }

    private void PrintErrorChat(string? message)
    {
        SeStringBuilder se = new SeStringBuilder().AddText("[PlayerSync] Error: " + message);
        _chatGui.PrintError(se.BuiltString);
    }

    private void PrintInfoChat(string? message, Pair? pair = null, string? linkName = null, string? ident = null)
        => PrintLinkedChat(ChatStyle.Info, message, pair, linkName, ident);

    private void PrintWarnChat(string? message, Pair? pair = null, string? linkName = null, string? ident = null)
        => PrintLinkedChat(ChatStyle.Warning, message, pair, linkName, ident);

    private void PrintPairRequestChat(string? message, Pair? pair = null, string? linkName = null, string? ident = null)
        => PrintLinkedChat(ChatStyle.Invite, message, pair, linkName, ident);

    private enum ChatStyle { Info, Warning, Invite }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values) if (!string.IsNullOrEmpty(v)) return v;
        return null;
    }

    // 1.15.5.1417: the player's name in a PSync chat line is a normal player link (PlayerPayload with name + home world),
    // so clicking or right-clicking it works like on a chat sender. The 1414 version read pair.Address on the mediator
    // thread; PairHandler.PlayerCharacter is nint.Zero unless it is read on the framework thread, so every line fell back
    // to plain text. The world lookup now runs on the framework thread (by content id, then ident, then exact name).
    // PlayerPayload.Encode() already writes the name text and the link terminator itself, so nothing is added after it.
    private void PrintLinkedChat(ChatStyle style, string? message, Pair? pair, string? linkName, string? ident)
    {
        message ??= string.Empty;
        var name = FirstNonEmpty(linkName, pair?.PlayerName, pair?.LastKnownPlayerName);
        var idx = string.IsNullOrEmpty(name) ? -1 : message.IndexOf(name, StringComparison.Ordinal);
        if (idx < 0)
        {
            _chatGui.Print(BuildChat(style, message, -1, null, 0));
            return;
        }

        var lookupIdent = FirstNonEmpty(ident, pair?.HashedCID, pair?.LastKnownHashedCID);
        _ = _dalamudUtilService.RunOnFrameworkThread(() =>
        {
            uint world = 0;
            try
            {
                world = _dalamudUtilService.GetHomeWorldIdOnFramework(pair?.ContendId ?? 0, lookupIdent, name);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Chat player link: home world lookup failed");
            }
            _chatGui.Print(BuildChat(style, message, idx, name, world));
        });
    }

    private SeString BuildChat(ChatStyle style, string message, int idx, string? name, uint world)
    {
        var linked = idx >= 0 && !string.IsNullOrEmpty(name) && world != 0;
        var before = linked ? message[..idx] : message;
        var after = linked ? message[(idx + name!.Length)..] : string.Empty;
        var se = new SeStringBuilder();
        if (style == ChatStyle.Warning)
        {
            se.AddText("[PlayerSync] ").AddUiForeground("Warning: " + before, 31).AddUiForegroundOff();
            if (linked)
            {
                se.Add(new PlayerPayload(name!, world));
                if (after.Length > 0) se.AddUiForeground(after, 31).AddUiForegroundOff();
            }
            return se.Build();
        }

        se.AddText("[PlayerSync] Info: ").AddItalics(before);
        if (linked)
        {
            se.Add(new PlayerPayload(name!, world));
            if (after.Length > 0) se.AddItalics(after);
        }
        if (style == ChatStyle.Invite && _inviteTogglePayload != null)
        {
            se.AddText(" ")
                .Add(_inviteTogglePayload)
                .AddUiForeground("Click here to view invites.", 37)
                .AddUiForegroundOff()
                .Add(RawPayload.LinkTerminator);
        }
        return se.Build();
    }

    private void PrintStageSaveChat(string message, string filename, string stageId)
    {
        _savedStageFilename = filename;
        _savedStageId = stageId;

        SeStringBuilder se = new();

        se.AddText($"[PlayerSync] Info: ");
        se.AddItalics(message);
        if (_stageUpdatePayload != null)
        {
            se.AddText(" ");
            se.Add(_stageUpdatePayload);
            se.AddUiForeground("Click here to upload the new version.", 37);
            se.AddUiForegroundOff();
            se.Add(RawPayload.LinkTerminator);
        }

        _chatGui.Print(se.Build());
    }

    private void ShowChat(NotificationMessage msg)
    {
        switch (msg.Type)
        {
            case NotificationType.Info:
                PrintInfoChat(msg.Message, msg.Pair, msg.PlayerLinkName, msg.PlayerIdent);
                break;

            case NotificationType.Warning:
                PrintWarnChat(msg.Message, msg.Pair, msg.PlayerLinkName, msg.PlayerIdent);
                break;

            case NotificationType.Error:
                PrintErrorChat(msg.Message);
                break;

            case NotificationType.Invite:
                PrintPairRequestChat(msg.Message, msg.Pair, msg.PlayerLinkName, msg.PlayerIdent);
                break;

            case NotificationType.Token:
                PrintInfoChat(msg.Message);
                break;

            case NotificationType.StageSaved:
                if (msg.UpdatedStageFilename != null && msg.UpdatedStageId != null)
                {
                    PrintStageSaveChat(msg.Message, msg.UpdatedStageFilename, msg.UpdatedStageId);
                }
                break;
        }
    }

    private void ShowNotification(NotificationMessage msg)
    {
        Logger.LogInformation("{msg}", msg.ToString());

        if (!_dalamudUtilService.IsLoggedIn) return;

        switch (msg.Type)
        {
            case NotificationType.Info:
                ShowNotificationLocationBased(msg, _configurationService.Current.InfoNotification);
                break;

            case NotificationType.Warning:
                ShowNotificationLocationBased(msg, _configurationService.Current.WarningNotification);
                break;

            case NotificationType.Error:
                ShowNotificationLocationBased(msg, _configurationService.Current.ErrorNotification);
                break;

            case NotificationType.Invite:
                ShowNotificationLocationBased(msg, _configurationService.Current.PairRequestNotification);
                break;

            case NotificationType.Token:
                ShowNotificationLocationBased(msg, _configurationService.Current.TokenRefreshNotification);
                break;

            case NotificationType.StageSaved:
                ShowNotificationLocationBased(msg, _stageConfigService.Current.StageSavedNotificationLocation);
                break;
        }
    }

    private void ShowNotificationLocationBased(NotificationMessage msg, NotificationLocation location)
    {
        switch (location)
        {
            case NotificationLocation.Toast:
                ShowToast(msg);
                break;

            case NotificationLocation.Chat:
                ShowChat(msg);
                break;

            case NotificationLocation.Both:
                ShowToast(msg);
                ShowChat(msg);
                break;

            case NotificationLocation.Nowhere:
                break;
        }
    }

    private void ShowToast(NotificationMessage msg)
    {
        Dalamud.Interface.ImGuiNotification.NotificationType dalamudType = msg.Type switch
        {
            NotificationType.Error => Dalamud.Interface.ImGuiNotification.NotificationType.Error,
            NotificationType.Warning => Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
            NotificationType.Info => Dalamud.Interface.ImGuiNotification.NotificationType.Info,
            NotificationType.Invite => Dalamud.Interface.ImGuiNotification.NotificationType.Info,
            NotificationType.Token => Dalamud.Interface.ImGuiNotification.NotificationType.Info,
            NotificationType.StageSaved => Dalamud.Interface.ImGuiNotification.NotificationType.Info,
            _ => Dalamud.Interface.ImGuiNotification.NotificationType.Info
        };

        var notification = _notificationManager.AddNotification(new Notification()
        {
            Content = msg.Message ?? string.Empty,
            Title = msg.Title,
            Type = dalamudType,
            Minimized = false,
            InitialDuration = msg.TimeShownOnScreen ?? TimeSpan.FromSeconds(3)
        });

        if (msg.Type == NotificationType.StageSaved && msg.UpdatedStageFilename != null && msg.UpdatedStageId != null)
        {
            notification.DrawActions += drawContext =>
            {
                if (ImGui.Button("Upload new version", new System.Numerics.Vector2(drawContext.MaxCoord.X - drawContext.MinCoord.X, ImGui.GetFrameHeight())))
                {
                    Mediator.Publish(new OpenStageUpdateWindow(msg.UpdatedStageId, msg.UpdatedStageFilename));
                }
                if (ImGui.IsItemHovered())
                {
                    using (ImRaii.Tooltip())
                    {
                        ImGui.TextUnformatted($"Opens the stage details window for stage {msg.UpdatedStageId} so you can upload the new version of {Path.GetFileName(msg.UpdatedStageFilename)}.");
                    }
                }
            };
        }
    }
}