using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.MareConfiguration;
using MareSynchronos.MareConfiguration.Configurations;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Mediator;
using MareSynchronos.UI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Numerics;
using CharacterData = MareSynchronos.API.Data.CharacterData;

namespace MareSynchronos.Services;

/// <summary>Raised by <c>PairHandler</c> whenever a sound file (.scd) is loaded for one of a pair's objects.</summary>
public record PairSoundLoadedMessage(Pair Pair, string GamePath, string FilePath) : SameThreadMessage;

/// <summary>Raised when a pair's sound state enters or leaves <see cref="SoundState.Active"/> (name tags need a redraw).</summary>
public record PairSoundActiveChangedMessage(Pair Pair) : SameThreadMessage;

/// <summary>Asks the Noise makers window to re-read its on/off setting.</summary>
public record NoiseMakersSettingChangedMessage : MessageBase;

/// <summary>Sound activity of a pair, see <see cref="SoundActivity"/>.</summary>
public enum SoundState
{
    /// <summary>No recent sound.</summary>
    None,
    /// <summary>Looping emote plus a modded sound file: "making noise now".</summary>
    Active,
    /// <summary>Stopped (or one-shot): icon starts at <see cref="SoundActivity.StoppedAlpha"/> and fades.</summary>
    Fading,
}

/// <summary>
/// Shared "is this pair making noise" rules for the main window speaker, the name-tag speaker and the Noise makers window.
///
/// State machine (state lives on <see cref="Pair"/>):
/// - ACTIVE: the character is in a looping state (<see cref="IsLoopingMode"/>) AND a .scd sound file was loaded via Penumbra.
///   Either order works: a sound that loads while already looping, or a loop that starts within
///   <see cref="LoopStartGraceSeconds"/> after the sound load. A loop alone (sit/doze with no sound file) is never ACTIVE.
/// - ACTIVE -> FADING when the loop ends (or the pair stops being visible), or when the pair's data is re-applied and
///   no longer contains any .scd replacement.
/// - Any .scd load while NOT in a looping state goes straight to FADING (never ACTIVE).
/// - FADING starts at <see cref="StoppedAlpha"/>; every new sound load restarts the fade.
///
/// LIMITATIONS (need in-game verification):
/// - The only sound signal is Penumbra's resource-load event for .scd files, so vanilla (unmodded) sounds are never seen.
/// - There is no emote start/stop event; the loop state is polled (<see cref="PollIntervalMs"/>) from <c>Character.Mode</c>.
///   Whether <c>EmoteLoop</c>/<c>InPositionLoop</c>/<c>Performance</c> match the emotes you care about is unverified.
/// - A one-shot sound played while the character happens to be in a silent looping pose (e.g. sitting) counts as ACTIVE
///   until the pose ends.
/// </summary>
public static class SoundActivity
{
    /// <summary>Icon alpha when the player stops making noise (start of the fade).</summary>
    public const float StoppedAlpha = 0.75f;

    /// <summary>Icon alpha at the end of the fade.</summary>
    public const float FadeEndAlpha = 0.15f;

    /// <summary>Seconds over which a fading icon goes from <see cref="StoppedAlpha"/> to <see cref="FadeEndAlpha"/> and then drops
    /// from the Noises lists. Fork setting VierAIConfig.NoisesFadeSeconds (10-300, default 60).</summary>
    public static double FadeSeconds => Math.Clamp(VierAIConfigStore.Current.NoisesFadeSeconds, 10, 300);

    /// <summary>How often (ms) the loop state of a visible pair is read.</summary>
    public const long PollIntervalMs = 250;

    /// <summary>A loop that starts within this many seconds after a sound load counts as "sound loaded while looping".</summary>
    public const double LoopStartGraceSeconds = 1.5;

    /// <summary>Private-use game glyph for the name tag speaker. Change here if it is not the speaker glyph in game.</summary>
    public const string NamePlateSpeakerGlyph = "\uE039";

    public static readonly SeStringTextColors NamePlateSpeakerColors = new(Foreground: 0x00FF00u, Glow: 0x010101u);

    /// <summary>Character modes treated as "looping" (a sound while in one of these is ACTIVE).</summary>
    public static bool IsLoopingMode(CharacterModes mode)
        => mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop or CharacterModes.Performance;

    public static bool IsActive(Pair pair) => pair.SoundState == SoundState.Active;

    /// <summary>
    /// Speaker glyph for the main window, Noises and analysis viewer: a plain speaker while fading, the speaker with a
    /// wave crescent coming out of it while ACTIVE (1.15.5.910). The name-tag glyph is separate.
    /// </summary>
    public static Dalamud.Interface.FontAwesomeIcon SpeakerIcon(Pair pair)
        => IsActive(pair) ? Dalamud.Interface.FontAwesomeIcon.VolumeDown : Dalamud.Interface.FontAwesomeIcon.VolumeOff;

    /// <summary>Speaker icon color: full brightness while ACTIVE, otherwise fading from 75% by time since the fade started.</summary>
    public static Vector4 IconColor(Pair pair, TimeSpan sinceFadeStart)
    {
        var green = Dalamud.Interface.Colors.ImGuiColors.HealerGreen;
        if (pair.SoundState == SoundState.Active) return green;

        var fade = (float)Math.Clamp(sinceFadeStart.TotalSeconds / FadeSeconds, 0d, 1d);
        var alpha = StoppedAlpha - ((StoppedAlpha - FadeEndAlpha) * fade);
        return new Vector4(green.X, green.Y, green.Z, alpha);
    }

    /// <summary>Tooltip text for the speaker icon.</summary>
    public static string Describe(Pair pair, TimeSpan since)
        => pair.SoundState == SoundState.Active
            ? $"Making noise now (modded sound in a looping emote), started {UiSharedService.ApproxElapsedTimeToString(since)}."
            : $"Last made modded noise {UiSharedService.ApproxElapsedTimeToString(since)}.";

    /// <summary>True while the pair should still be listed (ACTIVE, or FADING and not fully faded).</summary>
    public static bool IsListed(Pair pair, DateTimeOffset now)
        => pair.SoundState == SoundState.Active
            || (pair.SoundState == SoundState.Fading && pair.SoundStateSince != null
                && (now - pair.SoundStateSince.Value).TotalSeconds <= FadeSeconds);

    /// <summary>Listed in Noises: the normal rule, or muted by you after making noise (stays listed as muted while visible).</summary>
    public static bool IsListedOrMuted(Pair pair, DateTimeOffset now)
        => IsListed(pair, now) || (pair.SoundStateSince != null && (pair.UserPair?.OwnPermissions.IsDisableSounds() ?? false));

    /// <summary>Appends the green name-tag speaker if the option is on and the pair is ACTIVE.</summary>
    public static void AppendNamePlateSpeaker(SeStringBuilder builder, Pair pair, MareConfig config)
    {
        if (!config.NamePlatePairedSoundIndicator) return;
        if (!IsActive(pair)) return;

        builder.AddText(" ");
        builder.AddColoredText(NamePlateSpeakerGlyph, NamePlateSpeakerColors);
    }

    // All transitions return true when the ACTIVE flag changed (callers publish PairSoundActiveChangedMessage).

    /// <summary>1.15.5.1205: a one-shot sound keeps ACTIVE this long (each new load restarts it); sustained sounds last while emoting.</summary>
    public const double OneShotActiveSeconds = 15;

    /// <summary>A .scd file was loaded for the pair (any of its objects). <paramref name="sustained"/>: looping or long (see <see cref="ScdInfo"/>).</summary>
    public static bool OnSoundLoaded(Pair pair, DateTimeOffset now, bool sustained = false)
    {
        lock (pair)
        {
            pair.SoundLastLoadAt = now;
            pair.SoundLastLoadSustained = sustained;
            if (pair.SoundInLoop)
            {
                pair.SoundLoopHasSound = true;
                if (pair.SoundState == SoundState.Active)
                {
                    // Sustained wins; a one-shot extends a one-shot window but never shortens a sustained one.
                    if (sustained) pair.SoundActiveUntil = null;
                    else if (pair.SoundActiveUntil != null) pair.SoundActiveUntil = now.AddSeconds(OneShotActiveSeconds);
                    return false;
                }
                pair.SoundState = SoundState.Active;
                pair.SoundStateSince = now;
                pair.SoundActiveUntil = sustained ? null : now.AddSeconds(OneShotActiveSeconds);
                return true;
            }

            // One-shot: never ACTIVE, (re)start the fade.
            var wasActive = pair.SoundState == SoundState.Active;
            pair.SoundState = SoundState.Fading;
            pair.SoundStateSince = now;
            return wasActive;
        }
    }

    /// <summary>Loop state of the pair's character, from a poll.</summary>
    public static bool OnLoopPolled(Pair pair, bool inLoop, DateTimeOffset now)
    {
        lock (pair)
        {
            if (inLoop == pair.SoundInLoop) return false;
            pair.SoundInLoop = inLoop;

            if (inLoop)
            {
                pair.SoundLoopHasSound = false;
                // The sound file loads at about the same time the loop starts; accept it if it was just before this poll.
                if (pair.SoundLastLoadAt != null && (now - pair.SoundLastLoadAt.Value).TotalSeconds <= LoopStartGraceSeconds)
                {
                    pair.SoundLoopHasSound = true;
                    var wasActive = pair.SoundState == SoundState.Active;
                    pair.SoundState = SoundState.Active;
                    pair.SoundStateSince = pair.SoundLastLoadAt;
                    pair.SoundActiveUntil = pair.SoundLastLoadSustained ? null : pair.SoundLastLoadAt.Value.AddSeconds(OneShotActiveSeconds);
                    return !wasActive;
                }
                return false;
            }

            pair.SoundLoopHasSound = false;
            if (pair.SoundState != SoundState.Active) return false;
            pair.SoundState = SoundState.Fading;
            pair.SoundStateSince = now;
            return true;
        }
    }

    /// <summary>Reads the loop state for a visible pair (throttled). Pass <see cref="nint.Zero"/> if the pair is not visible.</summary>
    public static unsafe bool Poll(Pair pair, nint characterAddress, long nowTick, DalamudUtilService dalamudUtil)
    {
        if (characterAddress == nint.Zero)
        {
            // Not visible: treat as "loop ended" (ACTIVE -> FADING).
            return pair.SoundInLoop && OnLoopPolled(pair, false, DateTimeOffset.UtcNow);
        }

        if (nowTick < pair.SoundNextPollTick) return false;
        pair.SoundNextPollTick = nowTick + PollIntervalMs;

        // 1.15.5.1205: a one-shot sound only keeps ACTIVE for a short while, even if the player keeps emoting.
        if (EndOneShot(pair, DateTimeOffset.UtcNow)) return true;

        // A saved address stays non-zero after a despawn; only read it while it is still in the live object table.
        if (!dalamudUtil.IsLiveObject(characterAddress))
            return pair.SoundInLoop && OnLoopPolled(pair, false, DateTimeOffset.UtcNow);

        // Fork: ACTIVE also needs the character to be emoting right now: a looping mode AND a current emote id on its
        // EmoteController (Performance has no emote id and counts as is). Two field reads per poll, visible pairs only.
        var chara = (Character*)characterAddress;
        var mode = chara->Mode;
        var inLoop = IsLoopingMode(mode) && (mode == CharacterModes.Performance || chara->EmoteController.EmoteId != 0);
        return OnLoopPolled(pair, inLoop, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The pair's character unloaded (out of range, zone change, logout) or its handler is going away. Ends ACTIVE and the
    /// loop state right away, so the row fades from the unload time and a reappearing player does not start out as
    /// 'making noise now' with a stale loop flag. Mute is a server-side pair permission and is not touched.
    /// </summary>
    /// <summary>The pair's character (re)appeared: forget any prior loop state and re-check emote + sound on the next poll.</summary>
    public static void OnBecameVisible(Pair pair)
    {
        lock (pair)
        {
            pair.SoundNextPollTick = 0;
            pair.SoundInLoop = false;
            pair.SoundLoopHasSound = false;
        }
    }

    public static bool OnUnloaded(Pair pair)
    {
        lock (pair)
        {
            pair.SoundNextPollTick = 0;
            if (!pair.SoundInLoop && pair.SoundState != SoundState.Active) return false;
            pair.SoundInLoop = false;
            pair.SoundLoopHasSound = false;
            if (pair.SoundState != SoundState.Active) return false;
            pair.SoundState = SoundState.Fading;
            pair.SoundStateSince = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>The pair's data was (re)applied. If the sound files are gone from it, ACTIVE ends.</summary>
    public static bool OnDataApplied(Pair pair, CharacterData data)
    {
        // Next build: remember which sound files are the pair's own (their applied mod files), see IsOwnSound.
        pair.SoundOwnScdPaths = new HashSet<string>(data.FileReplacements.Values.SelectMany(l => l).SelectMany(r => r.GamePaths)
            .Where(p => p.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        if (pair.SoundState != SoundState.Active) return false;
        if (ContainsSound(data)) return false;

        lock (pair)
        {
            if (pair.SoundState != SoundState.Active) return false;
            pair.SoundState = SoundState.Fading;
            pair.SoundLoopHasSound = false;
            pair.SoundStateSince = DateTimeOffset.UtcNow;
            // Muted while noisy: an unmute restore may mark them noisy again right away (see TakeNoisyAtMute). Only for a
            // sustained sound (1205), and kept across the redraw/unload that a mute or unmute can cause.
            // Next build: no longer also requires the mute permission to still be set when this apply runs. A quick mute then
            // unmute could flip it back before the mute's apply finished, so the flag was never set and the icon never came back.
            // The flag is only read by an unmute/user restore (TakeNoisyAtMute) and cleared on unload.
            pair.SoundNoisyAtMute = pair.SoundActiveUntil == null;
            pair.SoundActiveUntil = null;
            return true;
        }
    }

    private static bool EndOneShot(Pair pair, DateTimeOffset now)
    {
        lock (pair)
        {
            if (pair.SoundState != SoundState.Active || pair.SoundActiveUntil == null || now < pair.SoundActiveUntil.Value) return false;
            pair.SoundState = SoundState.Fading;
            pair.SoundStateSince = now;
            pair.SoundActiveUntil = null;
            return true;
        }
    }

    /// <summary>True once if the pair was making noise when you muted them (cleared on read and on unload).</summary>
    public static bool TakeNoisyAtMute(Pair pair)
    {
        lock (pair)
        {
            var was = pair.SoundNoisyAtMute;
            pair.SoundNoisyAtMute = false;
            return was;
        }
    }

    /// <summary>Next build: true if this .scd game path is one of the pair's own synced sound files. A load that Penumbra
    /// attributes to the pair but that is not in their mod files (vanilla, or another actor's sound resolved in their context)
    /// does not count as them making noise.</summary>
    public static bool IsOwnSound(Pair pair, string gamePath)
    {
        var own = pair.SoundOwnScdPaths;
        return own == null || string.IsNullOrEmpty(gamePath) || own.Contains(gamePath);
    }

    public static bool ContainsSound(CharacterData data)
        => data.FileReplacements.Values.Any(list => list.Any(r => r.GamePaths.Any(p => p.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))));
}

/// <summary>
/// Remembers the most recent sound files per pair (for the Noise makers tooltip) and asks for a name tag redraw
/// when a pair enters or leaves the ACTIVE state, so the name tag speaker appears and disappears.
/// </summary>
public sealed class SoundActivityTracker : DisposableMediatorSubscriberBase, IHostedService
{
    private const int MaxRecentSoundsPerPair = 4;

    private readonly MareConfigService _configService;
    private readonly ConcurrentDictionary<string, List<(string GamePath, DateTimeOffset At)>> _recent = new(StringComparer.Ordinal);

    public SoundActivityTracker(ILogger<SoundActivityTracker> logger, MareMediator mediator, MareConfigService configService) : base(logger, mediator)
    {
        _configService = configService;
        Mediator.Subscribe<PairSoundLoadedMessage>(this, OnSoundLoaded);
        Mediator.Subscribe<PairSoundActiveChangedMessage>(this, (_) =>
        {
            if (_configService.Current.NamePlatePairedSoundIndicator) Mediator.Publish(new RedrawNameplateMessage());
        });
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The newest sound files loaded for this pair, newest first.</summary>
    public IReadOnlyList<(string GamePath, DateTimeOffset At)> GetRecentSounds(string uid)
    {
        if (!_recent.TryGetValue(uid, out var list)) return [];
        lock (list)
        {
            return [.. list];
        }
    }

    private void OnSoundLoaded(PairSoundLoadedMessage msg)
    {
        var uid = msg.Pair.UserData.UID;
        var now = DateTimeOffset.UtcNow;

        var list = _recent.GetOrAdd(uid, _ => []);
        lock (list)
        {
            list.RemoveAll(e => string.Equals(e.GamePath, msg.GamePath, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, (msg.GamePath, now));
            if (list.Count > MaxRecentSoundsPerPair) list.RemoveRange(MaxRecentSoundsPerPair, list.Count - MaxRecentSoundsPerPair);
        }
    }
}