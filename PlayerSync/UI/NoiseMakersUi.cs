using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.MareConfiguration;
using MareSynchronos.MareConfiguration.Models;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.UI.Components;
using MareSynchronos.WebAPI;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Text;

namespace MareSynchronos.UI;

/// <summary>
/// "Noises" (setting key and class keep the old "NoiseMakers" name): visible pairs whose modded sounds were loaded recently,
/// with the activity speaker, a mute/unmute button and a pause button. Shown while the Game UI setting is on.
///
/// Row rules:
/// - Rows appear when a visible, unpaused pair loads a sound and stay while the speaker is still fading.
/// - Pause removes the row immediately.
/// - Muted rows stay until <see cref="MutedRowLingerSeconds"/> pass without the mouse hovering the row; hovering resets the timer.
/// - The speaker follows the same brightness/fade rules as the main window (see <see cref="SoundActivity"/>).
///
/// Window behaviour (1.15.5.910):
/// - Auto-sizes to its rows.
/// - Invisible unless hovered: the background and title fade in on hover and out when the mouse leaves.
/// - Draws nothing while empty (fades out when the last row goes, fades in with the first).
/// - Hidden while the main window is open (the Noises folder in the main window shows the same pairs).
/// - Hovering a row does a temporary focus on that player; right-clicking a row (not a button) targets them.
/// </summary>
internal class NoiseMakersUi : WindowMediatorSubscriberBase
{
    /// <summary>How long a muted row stays listed without being hovered.</summary>
    public static double MutedRowLingerSeconds => Math.Clamp(MareSynchronos.Services.VierAIConfigStore.Current.NoisesMutedHideSeconds, 5, 60); // fork setting, default 15

    /// <summary>Seconds for the fade in/out of the window background/title and of the whole window appearing/disappearing.</summary>
    private const float FadeSeconds = 0.25f;

    private readonly MareConfigService _configService;
    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly SoundActivityTracker _tracker;
    private readonly WindowSystem _windowSystem;
    private readonly PairHoverTargeter _hover;

    // Rows kept after a mute/unmute click or for muted pairs: uid -> last hover/click time.
    private readonly Dictionary<string, DateTimeOffset> _pins = new(StringComparer.Ordinal);
    // Rows hidden until a newer sound load: uid -> time of dismissal.
    private readonly Dictionary<string, DateTimeOffset> _dismissed = new(StringComparer.Ordinal);

    private IWindow? _mainWindow;
    private List<Pair> _rows = new();
    private float _presence;      // 0..1: whole window appearing / disappearing
    private float _hoverFade;     // 0..1: background + title
    private bool _windowHovered;
    private readonly MutedRowLinger _mutedLinger = new();
    private readonly NoiseRowOrder _rowOrder = new();
    private Vector4 _baseText = new(1, 1, 1, 1);
    private int _pushedColors;
    private bool _pushedAlpha;
    private bool _openFilter; // 1.15.6.203: set by the title bar filter button, opens the filter dropdown on the next draw
    // 1.15.6.205: blacklist auto-mutes are tracked in NoiseAutoMute (lifted after the banned sound goes quiet, never touching
    // a mute you set by hand). Last lift check (throttled to once a second).
    private DateTimeOffset _lastAutoMuteCheck = DateTimeOffset.MinValue;

    public NoiseMakersUi(ILogger<NoiseMakersUi> logger, MareMediator mediator, PerformanceCollectorService performanceCollector,
        MareConfigService configService, PairManager pairManager, ApiController apiController, UiSharedService uiSharedService,
        SoundActivityTracker tracker, WindowSystem windowSystem) : base(logger, mediator, "Noises", performanceCollector)
    {
        _configService = configService;
        _pairManager = pairManager;
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _tracker = tracker;
        _windowSystem = windowSystem;
        _hover = new PairHoverTargeter(mediator, configService);

        Flags |= ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing;
        SizeConstraints = new()
        {
            MinimumSize = new(160, 0),
            MaximumSize = new(700, 600)
        };
        IsOpen = _configService.Current.ShowNoiseMakersWindow;

        // 1.15.6.203: settings gear and filter in the title bar (they fade with it); the filter opens a dropdown.
        TitleBarButtons =
        [
            new TitleBarButton
            {
                Icon = FontAwesomeIcon.Cog,
                Click = (_) => Mediator.Publish(new OpenSettingsGameUiMessage()),
                IconOffset = new(2, 1),
                ShowTooltip = () => ImGui.SetTooltip("Noises window settings"),
            },
            new TitleBarButton
            {
                Icon = FontAwesomeIcon.Filter,
                Click = (_) => _openFilter = true,
                IconOffset = new(2, 1),
                ShowTooltip = () => ImGui.SetTooltip("Noises filter: whitelist (green) and blacklist (red) words, auto-mute"),
            },
        ];
        Mediator.Subscribe<PairSoundLoadedMessage>(this, OnPairSoundLoaded);
        Mediator.Subscribe<PairMutedSoundPlayedMessage>(this, OnMutedPairSoundPlayed); // 1.15.6.204
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => CheckAutoMuteLifts()); // 1.15.6.205 (runs while the window is closed too)
        Mediator.Subscribe<NoiseMakersSettingChangedMessage>(this, (_) => IsOpen = _configService.Current.ShowNoiseMakersWindow);
    }

    /// <summary>1.15.6.203: auto-mute a player when a blacklisted sound loads for them (option, off by default).
    /// 1.15.6.205: tracked so the mute lifts after the sound goes quiet; a banned sound after a lift mutes them again.
    /// A pair already muted (by you) is left alone and not tracked; a pair whose mute you changed by hand this session is
    /// not auto-muted again until the plugin reloads.</summary>
    private void OnPairSoundLoaded(PairSoundLoadedMessage msg)
    {
        if (!MareSynchronos.Services.VierAIConfigStore.Current.NoisesAutoMuteBlacklisted || msg.Restored) return; // 1.15.6.206: not for an unmute restore
        var pair = msg.Pair;
        if (pair.UserPair == null || pair.IsPaused) return;
        if (NoiseFilter.Match(msg.GamePath) != NoiseFilterMatch.Blacklist) return;
        var uid = pair.UserData.UID;
        var now = DateTimeOffset.UtcNow;
        if (NoiseAutoMute.IsTracked(uid)) { NoiseAutoMute.BannedSeen(uid, now); return; } // mute already sent, server echo pending
        if (pair.UserPair.OwnPermissions.IsDisableSounds() || NoiseAutoMute.IsManuallyOverridden(uid)) return;
        _logger.LogInformation("Noises: auto-muting {pair} for blacklisted sound {path}", pair.UserData.AliasOrUID, msg.GamePath);
        NoiseAutoMute.Track(uid, now);
        NoiseRowButtons.ToggleMute(pair, _apiController, auto: true);
    }

    /// <summary>1.15.6.205: lift blacklist auto-mutes once no banned request came for NoiseAutoMute.QuietSeconds and the
    /// player's emote loop has ended (whichever is later). A tracked pair found unmuted (you unmuted it some other way) is
    /// dropped and left alone.</summary>
    private void CheckAutoMuteLifts()
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastAutoMuteCheck).TotalSeconds < 1) return;
        _lastAutoMuteCheck = now;
        RefreshBlacklistSilencing();
        foreach (var (uid, mutedAt, lastBanned) in NoiseAutoMute.Snapshot())
        {
            var pair = _pairManager.GetPairByUID(uid);
            if (pair?.UserPair == null) continue; // offline / not paired right now: keep it until they are back
            if (!pair.UserPair.OwnPermissions.IsDisableSounds())
            {
                if ((now - mutedAt).TotalSeconds < NoiseAutoMute.ServerEchoGraceSeconds) continue;
                _logger.LogInformation("Noises: {pair} was unmuted outside the auto-mute; no longer tracked", pair.UserData.AliasOrUID);
                NoiseAutoMute.Forget(uid, manual: true);
                pair.PSyncUpdateMuteRedirects(); // 1.15.6.207: overridden now: its blacklisted sounds play normally
                continue;
            }
            if (pair.IsPaused) continue;
            if ((now - lastBanned).TotalSeconds < NoiseAutoMute.QuietSeconds) continue;
            if (pair.SoundInLoop) continue; // still in the emote loop the sound came with
            _logger.LogInformation("Noises: lifting auto-mute of {pair}: no blacklisted sound for {s:0} s and no emote loop",
                pair.UserData.AliasOrUID, (now - lastBanned).TotalSeconds);
            NoiseAutoMute.Forget(uid, manual: false);
            NoiseRowButtons.ToggleMute(pair, _apiController, auto: true);
            _pins[uid] = now; // keep the row listed so you see who came back
        }
    }

    private string? _blacklistSignature;
    private string? _whitelistSignature; // 1.15.6.210

    /// <summary>1.15.6.206: the blacklist or the auto-mute option changed: re-apply every pair's per-path silencing (cheap when
    /// nothing changed for a pair, see PSyncUpdateMuteRedirects).</summary>
    private void RefreshBlacklistSilencing()
    {
        var cfg = MareSynchronos.Services.VierAIConfigStore.Current;
        var sig = cfg.NoisesAutoMuteBlacklisted + "|" + NoiseFilter.BlacklistSignature(); // 1.15.6.211: snapshot (audit Q1)
        var now = DateTimeOffset.UtcNow;
        // 1.15.6.210: a whitelist edit changes which sounds a muted pair keeps: re-apply their data and redirects.
        var white = NoiseFilter.WhitelistSignature();
        if (!string.Equals(white, _whitelistSignature, StringComparison.Ordinal))
        {
            var firstWhite = _whitelistSignature == null;
            _whitelistSignature = white;
            if (!firstWhite)
            {
                _redirectRetry.Clear();
                foreach (var pair in _pairManager.GetAllPairs())
                {
                    if (pair.UserPair == null) continue;
                    ApplyRedirects(pair, now);
                    if (!pair.IsPaused && pair.UserPair.OwnPermissions.IsDisableSounds() && !pair.UserPair.OtherPermissions.IsDisableSounds())
                    {
                        _logger.LogDebug("Noises: whitelist changed, re-applying muted {pair}", pair.UserData.AliasOrUID);
                        pair.ReapplyCachedData();
                    }
                    else if (!pair.IsPaused && pair.IsVisible && pair.PSyncBlacklistStripOutdated()) // 1.15.6.217: whitelist beats the blacklist strip
                    {
                        _logger.LogDebug("Noises: whitelist changed, re-applying {pair} (blacklisted sounds stripped at apply)", pair.UserData.AliasOrUID);
                        pair.ReapplyCachedData();
                    }
                }
            }
        }
        if (!string.Equals(sig, _blacklistSignature, StringComparison.Ordinal))
        {
            var first = _blacklistSignature == null;
            _blacklistSignature = sig;
            _redirectRetry.Clear(); // a filter change resets every backoff
            if (first && !cfg.NoisesAutoMuteBlacklisted) return; // nothing to undo at startup
            foreach (var pair in _pairManager.GetAllPairs())
            {
                ApplyRedirects(pair, now);
                // 1.15.6.217: blacklisted sounds are also stripped at apply; re-apply visible pairs whose stripped set changed.
                if (!first && pair.UserPair != null && !pair.IsPaused && pair.IsVisible && pair.PSyncBlacklistStripOutdated())
                {
                    _logger.LogDebug("Noises: blacklist or auto-mute changed, re-applying {pair}", pair.UserData.AliasOrUID);
                    pair.ReapplyCachedData();
                }
            }
            return;
        }
        if (_redirectRetry.Count == 0) return;
        // 1.15.6.208: per-pair retry of a failed Penumbra apply, 1 s doubling to 60 s, reset on success or a filter change.
        foreach (var (uid, (due, _)) in _redirectRetry.ToList())
        {
            if (now < due) continue;
            var pair = _pairManager.GetPairByUID(uid);
            if (pair == null) { _redirectRetry.Remove(uid); continue; }
            ApplyRedirects(pair, now);
        }
    }

    private void ApplyRedirects(Pair pair, DateTimeOffset now)
    {
        var uid = pair.UserData.UID;
        if (pair.PSyncTryUpdateMuteRedirectsNow()) { _redirectRetry.Remove(uid); return; }
        var delay = _redirectRetry.TryGetValue(uid, out var prev) ? Math.Min(prev.DelaySeconds * 2, RedirectRetryMaxSeconds) : 1;
        _redirectRetry[uid] = (now.AddSeconds(delay), delay);
    }

    private const double RedirectRetryMaxSeconds = 60;
    // 1.15.6.208: uid -> next retry time and current backoff (replaces the 207 every-second retry of all pairs).
    private readonly Dictionary<string, (DateTimeOffset Due, double DelaySeconds)> _redirectRetry = new(StringComparer.Ordinal);

    // 1.15.6.204: auto-unmute bookkeeping. uid -> last blacklisted muted play; uid -> last auto-unmute (one per trigger).
    private readonly Dictionary<string, DateTimeOffset> _mutedBlacklistSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _autoUnmutedAt = new(StringComparer.Ordinal);
    private const double BlacklistBlockSeconds = 15;
    private const double UnmuteDebounceSeconds = 30;

    /// <summary>1.15.6.204: a player you muted played one of their sounds. Whitelisted -> unmute once (blacklist wins); never re-mutes.</summary>
    private void OnMutedPairSoundPlayed(PairMutedSoundPlayedMessage msg)
    {
        var pair = msg.Pair;
        if (pair.UserPair == null || pair.IsPaused || !pair.UserPair.OwnPermissions.IsDisableSounds()) return;
        var uid = pair.UserData.UID;
        var now = DateTimeOffset.UtcNow;
        var match = NoiseFilter.Match(msg.GamePath);
        // 1.15.6.205: a banned request from an auto-muted pair restarts its quiet timer (whatever the unmute option says).
        if (match == NoiseFilterMatch.Blacklist) NoiseAutoMute.BannedSeen(uid, now);
        if (!MareSynchronos.Services.VierAIConfigStore.Current.NoisesAutoUnmuteWhitelisted) return;
        if (match == NoiseFilterMatch.Blacklist) { _mutedBlacklistSeen[uid] = now; return; }
        if (match != NoiseFilterMatch.Whitelist) return;
        if (NoiseAutoMute.IsTracked(uid))
        {
            _logger.LogInformation("Noises: not auto-unmuting {pair} ({path}): auto-muted for a blacklisted sound (lifts by itself when it goes quiet)", pair.UserData.AliasOrUID, msg.GamePath);
            return;
        }
        if (_mutedBlacklistSeen.TryGetValue(uid, out var black) && (now - black).TotalSeconds <= BlacklistBlockSeconds)
        {
            _logger.LogInformation("Noises: not auto-unmuting {pair} ({path}): blacklisted sound in the last {s} s", pair.UserData.AliasOrUID, msg.GamePath, BlacklistBlockSeconds);
            return;
        }
        if (_autoUnmutedAt.TryGetValue(uid, out var last) && (now - last).TotalSeconds <= UnmuteDebounceSeconds) return;
        _autoUnmutedAt[uid] = now;
        _logger.LogInformation("Noises: auto-unmuting {pair} for whitelisted sound {path}", pair.UserData.AliasOrUID, msg.GamePath);
        NoiseRowButtons.ToggleMute(pair, _apiController, auto: true); // muted -> unmuted (server permission, same as clicking the speaker)
        _pins[uid] = now; // keep the row listed so you see who came back
    }

    public override void OnClose()
    {
        // Closing the window with its X turns the setting off.
        if (_configService.Current.ShowNoiseMakersWindow)
        {
            _configService.Current.ShowNoiseMakersWindow = false;
            _configService.Save();
        }
        base.OnClose();
    }

    private bool MainWindowOpen()
    {
        _mainWindow ??= _windowSystem.Windows.FirstOrDefault(w => w is CompactUi);
        return _mainWindow?.IsOpen ?? false;
    }

    private static float Approach(float value, float target, float step)
        => value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

    // Runs every frame while the window is open (even while DrawConditions hides it), so fades and rows keep tracking.
    public override void Update()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = CollectRows(now);
        var dt = ImGui.GetIO().DeltaTime;
        var step = dt / FadeSeconds;

        if (rows.Count > 0) _rows = rows; // keep the last rows on screen while fading out
        _presence = Approach(_presence, rows.Count > 0 ? 1f : 0f, step);
        if (rows.Count == 0 && _presence <= 0f) _rows = new();

        var mainOpen = MainWindowOpen();
        if (mainOpen) _windowHovered = false;
        _hoverFade = Approach(_hoverFade, (_windowHovered || !MareSynchronos.Services.VierAIConfigStore.Current.NoisesOnlyShowOnHover) ? 1f : 0f, step); // 1706: hover-only is optional
    }

    public override bool DrawConditions() => _rows.Count > 0 && !MainWindowOpen();

    public override unsafe void PreDraw()
    {
        _pushedColors = 0;
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(_presence, 0f, 1f));
        _pushedAlpha = true;

        _baseText = *ImGui.GetStyleColorVec4(ImGuiCol.Text);

        // Background, border and title bar follow the hover fade. The title text is the Text color, so it is faded here too
        // and DrawInternal switches back to the normal text color for the rows.
        ImGuiCol[] faded = [ImGuiCol.WindowBg, ImGuiCol.Border, ImGuiCol.TitleBg, ImGuiCol.TitleBgActive, ImGuiCol.TitleBgCollapsed, ImGuiCol.Text];
        foreach (var col in faded)
        {
            var c = *ImGui.GetStyleColorVec4(col);
            c.W *= _hoverFade;
            ImGui.PushStyleColor(col, c);
            _pushedColors++;
        }
    }

    public override void PostDraw()
    {
        if (_pushedColors > 0) ImGui.PopStyleColor(_pushedColors);
        if (_pushedAlpha) ImGui.PopStyleVar();
        _pushedColors = 0;
        _pushedAlpha = false;
    }

    private readonly HashSet<string> _scratchUids = new(StringComparer.Ordinal);
    private readonly List<string> _scratchRemove = new();
    private readonly List<Pair> _drawRows = new();

    private List<Pair> CollectRows(DateTimeOffset now)
    {
        var rows = new List<Pair>();
        // Fork perf: one visible-pairs list per collection (was three) and reused scratch collections below.
        var visiblePairs = _pairManager.GetVisiblePairs();

        foreach (var pair in visiblePairs)
        {
            var uid = pair.UserData.UID;
            if (pair.UserPair == null || pair.IsPaused) continue;
            var last = pair.SoundStateSince;
            if (last == null) continue;
            // 1.15.6.202: its sounds only play on game sound channels you muted (or set to 0): hidden until you unmute one.
            if (SoundChannelLog.HiddenByGameMute(uid, now)) continue;

            var hasDismissal = _dismissed.TryGetValue(uid, out var dismissedAt);
            var muted = pair.UserPair.OwnPermissions.IsDisableSounds();
            // 1.15.6.206: muted only by the blacklist auto-mute: not listed (a mute you set by hand is never tracked there).
            if (muted && NoiseAutoMute.IsTracked(uid)) { _pins.Remove(uid); continue; }
            // Muted players stay listed (as muted) while visible; unmuting brings them back as active when the sound reloads.
            var loadBased = muted ? SoundActivity.IsListedOrMuted(pair, now) : SoundActivity.IsListed(pair, now) && (!hasDismissal || last.Value > dismissedAt);
            // Fork (VierAI 2026-10-24): muted rows drop off NoisesMutedHideSeconds (default 15 s) after the row was last hovered (MutedRowLinger.cs).
            if (muted)
            {
                _pins.Remove(uid);
                if (loadBased && _mutedLinger.Keep(uid, true, now)) rows.Add(pair);
                continue;
            }
            _mutedLinger.Keep(uid, false, now);

            var pinned = _pins.ContainsKey(uid);
            if (!loadBased && !pinned) continue;

            if (pinned && (now - _pins[uid]).TotalSeconds > MutedRowLingerSeconds)
            {
                _pins.Remove(uid);
                // Unmuted rows just go back to the normal "recently made noise" rule.
                if (!loadBased) continue;
            }

            rows.Add(pair);
        }

        // Pins only make sense while the player is visible: drop them on unload (a muted player that comes back gets a fresh
        // linger timer). Dismissals stay keyed by UID across unload/reload so a dismissed muted row doesn't pop back until a
        // newer sound loads; they are forgotten once older than the fade (by then the row would be gone anyway).
        if (_pins.Count > 0)
        {
            _scratchUids.Clear();
            foreach (var p in visiblePairs) _scratchUids.Add(p.UserData.UID);
            _scratchRemove.Clear();
            foreach (var uid in _pins.Keys) if (!_scratchUids.Contains(uid)) _scratchRemove.Add(uid);
            foreach (var uid in _scratchRemove) _pins.Remove(uid);
        }
        _mutedLinger.Prune(visiblePairs);
        if (_dismissed.Count > 0)
        {
            _scratchRemove.Clear();
            foreach (var d in _dismissed) if ((now - d.Value).TotalSeconds > SoundActivity.FadeSeconds) _scratchRemove.Add(d.Key);
            foreach (var uid in _scratchRemove) _dismissed.Remove(uid);
        }

        return _rowOrder.Update(rows, _windowHovered); // fork: stable order, no removals while hovered (NoiseRowOrder.cs)
        return rows;
    }

    private static string RowName(Pair pair, bool muted)
    {
        var name = string.IsNullOrWhiteSpace(pair.PlayerName) ? pair.UserData.AliasOrUID : pair.PlayerName;
        return name; // fork: no "(muted)" label, the mute button shows it
    }

    protected override void DrawInternal()
    {
        _windowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.ChildWindows);

        // Rows use the normal text color; only the title was drawn with the faded one.
        using var text = ImRaii.PushColor(ImGuiCol.Text, _baseText);

        var now = DateTimeOffset.UtcNow;
        var rows = _drawRows;
        rows.Clear();
        foreach (var p in _rows) if (p.UserPair != null && p.SoundStateSince != null) rows.Add(p);

        // 1.15.6.203: filter dropdown (title bar filter button).
        if (_openFilter) { ImGui.OpenPopup("##noisesfilter"); _openFilter = false; }
        if (ImGui.BeginPopup("##noisesfilter"))
        {
            NoiseFilterEditor.Draw(_uiSharedService, "nw");
            ImGui.EndPopup();
        }
        if (rows.Count == 0) return;

        // Auto-size: every row is as wide as the widest one, buttons right-aligned inside that width.
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var muteWidth = NoiseRowButtons.MuteWidth(_uiSharedService);
        var pauseWidth = NoiseRowButtons.PauseWidth(_uiSharedService);
        var speakerWidth = _uiSharedService.GetIconSize(FontAwesomeIcon.VolumeDown).X;
        // Fork: fixed width (name column sized for a typical long name, NoiseNameFit.cs); longer names are clipped with an ellipsis.
        var rowWidth = speakerWidth + spacingX + NoiseNameFit.NameWidth() + (spacingX * 2) + pauseWidth; // 1210: no mute button (speaker toggles)

        // 1.15.6.203: the settings gear moved to the title bar (TitleBarButtons).

        _hover.BeginFrame();
        foreach (var pair in rows)
        {
            DrawRow(pair, now, rowWidth, muteWidth, pauseWidth);
        }
        _hover.EndFrame();
    }

    private void DrawRow(Pair pair, DateTimeOffset now, float rowWidth, float muteWidth, float pauseWidth)
    {
        var uid = pair.UserData.UID;
        using var id = ImRaii.PushId(uid);

        var perm = pair.UserPair!.OwnPermissions;
        var muted = perm.IsDisableSounds();
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var startX = ImGui.GetCursorPosX();
        var endX = startX + rowWidth;

        var rowMin = ImGui.GetCursorScreenPos();
        var rowMax = new Vector2(rowMin.X + rowWidth, rowMin.Y + ImGui.GetFrameHeight());

        // Activity speaker, same colors as the main window (with the wave crescent while active).
        var since = now - (pair.SoundStateSince ?? now);
        ImGui.AlignTextToFramePadding();
        var speaker = NoiseRowButtons.Speaker(pair, since, _uiSharedService, _apiController); // 1210: click = mute toggle

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        var (shownName, truncated) = NoiseNameFit.Fit(RowName(pair, muted), NoiseNameFit.NameWidth());
        var glow = NoiseGlow.ColorFor(pair, _tracker, now); // 1706
        if (glow != null) NoiseGlow.Draw(ImGui.GetCursorScreenPos() + new Vector2(0, ImGui.GetStyle().FramePadding.Y), shownName, glow.Value);
        ImGui.TextUnformatted(shownName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(truncated ? RowName(pair, muted) + "\n" + BuildSoundsTooltip(pair, now) : BuildSoundsTooltip(pair, now));
        }

        // Mute / unmute, pause (shared with the Noises folder in the main window).
        // Fork: buttons only while the window is hovered (not in its transparent monitoring state). When hidden, a same-height
        // placeholder keeps the row height, width and order unchanged.
        var buttons = (_windowHovered || !MareSynchronos.Services.VierAIConfigStore.Current.NoisesOnlyShowOnHover)
            ? NoiseRowButtons.Draw(pair, endX, _uiSharedService, _apiController, Mediator)
            : NoiseRowButtons.Placeholder(endX, _uiSharedService);
        var buttonHovered = buttons.Hovered || speaker.Hovered;
        if (speaker.MuteClicked) _pins[uid] = now;
        if (buttons.PauseClicked)
        {
            _pins.Remove(uid);
            _dismissed[uid] = now; // the row disappears right away; the server round trip for the pause comes later
        }

        var rowHovered = ImGui.IsMouseHoveringRect(rowMin, rowMax) && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        // Hovering anywhere on the row resets the muted-row timer.
        if (_pins.ContainsKey(uid) && rowHovered)
        {
            _pins[uid] = now;
        }
        if (rowHovered) _mutedLinger.Touch(uid, now);

        // Temporary focus on hover, right-click (not on a button) to target.
        _hover.Row(pair, rowHovered, buttonHovered);
    }
    private string BuildSoundsTooltip(Pair pair, DateTimeOffset now)
    {
        var sounds = _tracker.GetRecentSounds(pair);
        // 1.15.6.206: says what is true instead of "recorded yet" (files expire after NoisesRecentExpireMinutes).
        if (sounds.Count == 0)
            return $"No sound files in the last {SoundActivityTracker.RecentExpireMinutes} min.";

        var sb = new StringBuilder("Recent sound files:");
        var anyBlacklistedHighlightOnly = false; // 1.15.6.217
        foreach (var (gamePath, at, playing) in sounds)
        {
            sb.Append('\n').Append(Path.GetFileName(gamePath));
            // 1.15.6.202: game sound channel tag, and whether your game sound settings silence it.
            var ch = SoundChannelLog.ChannelsOf(pair.UserData.UID, gamePath);
            if (ch != null)
            {
                sb.Append(" [").Append(SoundChannelMap.Label(ch.Value, gamePath)).Append(']');
                if (SoundChannelState.IsInaudible(ch.Value)) sb.Append(" muted in game");
            }
            switch (NoiseFilter.Match(gamePath)) // 1.15.6.203
            {
                case NoiseFilterMatch.Whitelist: sb.Append(" (whitelist)"); break;
                case NoiseFilterMatch.Blacklist: // 1.15.6.217: say when the blacklist only highlights
                    sb.Append(MareSynchronos.Services.VierAIConfigStore.Current.NoisesAutoMuteBlacklisted ? " (blacklist)" : " (blacklist, highlight only, auto-mute off)");
                    anyBlacklistedHighlightOnly |= !MareSynchronos.Services.VierAIConfigStore.Current.NoisesAutoMuteBlacklisted;
                    break;
            }
            if (playing) sb.Append(" (playing, loaded ").Append(UiSharedService.ApproxElapsedTimeToString(now - at)).Append(" ago)"); // 1.15.6.206
            else sb.Append(" (").Append(UiSharedService.ApproxElapsedTimeToString(now - at)).Append(" ago)");
        }
        if (anyBlacklistedHighlightOnly)
            sb.Append("\n\nBlacklisted sounds still play: auto-mute is off. Turn it on with the \"Turn on auto-mute\" button in the Noises filter (funnel button).");
        return sb.ToString();
    }
}