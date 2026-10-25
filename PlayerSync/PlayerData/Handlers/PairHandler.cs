using MareSynchronos.API.Data.Enum;
using MareSynchronos.MareConfiguration;
using MareSynchronos.MareConfiguration.Configurations;
using MareSynchronos.PlayerData.Factories;
using MareSynchronos.PlayerData.Handlers;
using MareSynchronos.Services;
using MareSynchronos.Services.Events;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;
using Microsoft.Extensions.Logging;

namespace MareSynchronos.PlayerData.Pairs;

public sealed class PairHandler : DisposableMediatorSubscriberBase
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly ServerConfigurationManager _serverConfigManager;

    private GameObjectHandler? _charaHandler;
    private volatile bool _isVisible;

    public PairHandler(ILogger<PairHandler> logger, Pair pair,
        GameObjectHandlerFactory gameObjectHandlerFactory,
        DalamudUtilService dalamudUtil, MareMediator mediator,
        ServerConfigurationManager serverConfigManager, MareConfigService configService
        ) : base(logger, mediator)
    {
        Pair = pair;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _dalamudUtil = dalamudUtil;
        _serverConfigManager = serverConfigManager;

        if (!configService.Current.DebugDisableSoundIndicators)
        {
            // Fork perf: registered with PairSoundRouter in Initialize (one lookup per sound load instead of a broadcast).
            _soundHandler = OnPenumbraResourceLoaded;
        }

        // Fork: poll the emote loop state of visible pairs for the sound activity indicator (throttled inside).
        Mediator.Subscribe<FrameworkUpdateMessage>(this, (_) =>
        {
            var nowTick = Environment.TickCount64;
            bool visible = IsVisible && _charaHandler != null;
            // Fork perf: check the poll throttle before the object table lookup (PlayerCharacter) so throttled frames cost nothing.
            // Not-visible pairs still go through Poll every frame so an active loop ends as soon as they leave view.
            if (visible && nowTick < Pair.SoundNextPollTick) return;
            if (SoundActivity.Poll(Pair, visible ? PlayerCharacter : nint.Zero, nowTick, _dalamudUtil))
                Mediator.Publish(new PairSoundActiveChangedMessage(Pair));
        });
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                // Fork: end the sound ACTIVE/loop state as soon as the character unloads.
                if (!value && SoundActivity.OnUnloaded(Pair)) Mediator.Publish(new PairSoundActiveChangedMessage(Pair));
                if (value) SoundActivity.OnBecameVisible(Pair);
                string text = "User Visibility Changed, now: " + (_isVisible ? "Is Visible" : "Is not Visible");
                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational, text)));
                Mediator.Publish(new RefreshUiMessage());
            }
        }
    }
    public bool IsModelReady => IsVisible && (_charaHandler?.ModelReady ?? false);
    public Pair Pair { get; private set; }
    public GameObjectHandler? CharacterHandler => _charaHandler;
    public nint PlayerCharacter => _dalamudUtil.IsOnFrameworkThread && IsVisible && _dalamudUtil.TryGetPlayerAddressAndIndexOnFrameworkByContentId(Pair.ContendId, out var address, out _) ? address : nint.Zero;
    public ulong PlayerCharacterId => _charaHandler?.PlayerCharacterId ?? 0;
    public string? PlayerName { get; private set; }
    public ulong ContentId => _charaHandler?.ContentId ?? 0;
    public string PlayerNameHash => Pair.HashedCID;
    public override string ToString()
    {
        return Pair == null ? base.ToString() ?? string.Empty : Pair.UserData.AliasOrUID + ":" + PlayerName + ":" + (IsVisible ? "HasChar" : "NoChar");
    }

    // Fork: sound load callback registered with PairSoundRouter (null when sound indicators are disabled).
    private readonly Action<PenumbraSoundResourceLoadMessage>? _soundHandler;
    private ulong _soundContentId;

    public async Task Initialize(string name, ulong contendId)
    {
        if (_soundHandler != null) { _soundContentId = contendId; PairSoundRouter.Register(contendId, _soundHandler); }
        _charaHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () =>
        {
            _dalamudUtil.TryGetPlayerAddressAndIndexOnFrameworkByContentId(contendId, out var address, out _);

            return address;
        }, contendId, isOwnObject: false).ConfigureAwait(false);
        PlayerName = name;
        _serverConfigManager.AutoPopulateNoteForUid(Pair.UserData.UID, name);
    }

    public void UpdateVisibility(bool visible)
    {
        IsVisible = visible;
    }

    private void OnPenumbraResourceLoaded(PenumbraSoundResourceLoadMessage resourceLoad)
    {
        if (!_dalamudUtil.IsOnFrameworkThread || !IsVisible || resourceLoad.ContentId != Pair.ContendId)
        {
            return;
        }
        // Next build: only the pair's own synced sound files count (see SoundActivity.IsOwnSound).
        if (!SoundActivity.IsOwnSound(Pair, resourceLoad.GamePath)) return;

        Pair.LastLoadedSoundSinceRedrawTime = DateTimeOffset.UtcNow;
        Pair.LastLoadedSoundSinceRedraw = resourceLoad.FilePath;

        // Fork: sound activity state machine and Noise makers window.
        // 1205: one-shot vs sustained from the SCD header (cached; read off the game thread the first time).
        var filePath = resourceLoad.FilePath;
        var sustained = ScdInfo.TryGetCached(filePath);
        var activeChanged = SoundActivity.OnSoundLoaded(Pair, DateTimeOffset.UtcNow, sustained ?? false);
        Mediator.Publish(new PairSoundLoadedMessage(Pair, resourceLoad.GamePath, filePath));
        if (activeChanged) Mediator.Publish(new PairSoundActiveChangedMessage(Pair));
        if (sustained == null)
        {
            var loadedAt = Pair.SoundLastLoadAt;
            _ = ScdInfo.ClassifyAsync(filePath).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || !t.Result) return;
                _ = _dalamudUtil.RunOnFrameworkThread(() =>
                {
                    // Still the same load: upgrade it to sustained.
                    if (Pair.SoundLastLoadAt != loadedAt) return;
                    if (SoundActivity.OnSoundLoaded(Pair, loadedAt ?? DateTimeOffset.UtcNow, sustained: true)) Mediator.Publish(new PairSoundActiveChangedMessage(Pair));
                });
            }, TaskScheduler.Default);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        Mediator.Publish(new PairOfflineMessage(Pair));
        if (_soundHandler != null) PairSoundRouter.Unregister(_soundContentId, _soundHandler);
        // Fork: the Pair outlives this handler; don't leave it stuck in ACTIVE/looping (the poll stops with the handler).
        if (SoundActivity.OnUnloaded(Pair)) Mediator.Publish(new PairSoundActiveChangedMessage(Pair));

        var name = PlayerName;
        Logger.LogDebug("Disposing {name} ({user})", name, Pair);
        try
        {
            _charaHandler?.Dispose();
            _charaHandler = null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error on disposal of {name}", name);
        }
        finally
        {
            PlayerName = null;
        }
    }
}
