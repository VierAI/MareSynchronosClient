using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Microsoft.Extensions.Logging;

namespace MareSynchronos.Services;

/// <summary>
/// Bug fix helper: after a pair's character was redrawn (unmute, "Reload last data", visibility), the looping emote keeps
/// playing but its sound is not started again, because the sound is triggered by the emote's start, not by the loop.
/// This replays the character's current looping emote (same id, same target, no sit-down animation) so the sound starts again.
///
/// LIMITATIONS (need in-game verification):
/// - <c>EmoteController.PlayEmote</c> is the game's own "play emote on a non-local character" function; whether it restarts an
///   emote that is already playing (and so re-triggers the sound) was not verified in game.
/// - Only EmoteLoop / InPositionLoop with a non-zero emote id are handled. Performance (instruments) is never touched.
/// </summary>
public static class EmoteSoundRestart
{
    /// <summary>Wait after the redraw so the game has restored the emote before it is replayed.</summary>
    public const int SettleDelayMs = 400;

    /// <summary>Gap between stopping the emote and playing it again in a full restart (a few frames).</summary>
    public const int StopToPlayDelayMs = 150;

    /// <summary>Replays the looping emote of the character at <paramref name="address"/>. Call on the framework thread.</summary>
    public static unsafe bool TryReplayEmote(ILogger logger, nint address, string who)
    {
        if (address == nint.Zero) return false;

        var chara = (Character*)address;
        var mode = chara->Mode;
        var emoteId = chara->EmoteController.EmoteId;
        if (mode is not (CharacterModes.EmoteLoop or CharacterModes.InPositionLoop) || emoteId == 0)
        {
            logger.LogDebug("Emote sound restart for {who}: skipped (mode {mode}, emote {emote})", who, mode, emoteId);
            return false;
        }

        var option = new EmoteController.PlayEmoteOption
        {
            TargetId = chara->EmoteController.Target,
            DisableLogMessage = true,
            DisableSitDownAnimation = true,
        };
        option.VirtualTable = EmoteController.PlayEmoteOption.StaticVirtualTablePointer;
        var ok = chara->EmoteController.PlayEmote(emoteId, &option);
        logger.LogInformation("Emote sound restart for {who}: replayed emote {emote} (mode {mode}), result {ok}", who, emoteId, mode, ok);
        return ok;
    }

    /// <summary>
    /// Full restart, step 1: stops the looping emote (back to normal mode) and returns its id and target, or 0 when the character
    /// is not in a looping emote. Replaying the same emote while it still runs can keep the current timeline, so sounds started by
    /// the emote's VFX (C012 keys spawning an .avfx that plays the .scd) are not spawned again. Call on the framework thread.
    /// </summary>
    public static unsafe ushort TryStopLoopingEmote(ILogger logger, nint address, string who, out ulong target)
    {
        target = 0;
        if (address == nint.Zero) return 0;
        var chara = (Character*)address;
        var mode = chara->Mode;
        var emoteId = chara->EmoteController.EmoteId;
        if (mode is not (CharacterModes.EmoteLoop or CharacterModes.InPositionLoop) || emoteId == 0) return 0;
        target = chara->EmoteController.Target;
        chara->SetMode(CharacterModes.Normal, 0);
        logger.LogDebug("Emote sound restart for {who}: stopped emote {emote} (mode {mode}) for a full restart", who, emoteId, mode);
        return emoteId;
    }

    /// <summary>Full restart, step 2: plays the emote from the start (intro included, so its VFX and sounds spawn again).</summary>
    public static unsafe bool PlayEmoteFromStart(ILogger logger, nint address, string who, ushort emoteId, ulong target)
    {
        if (address == nint.Zero || emoteId == 0) return false;
        var chara = (Character*)address;
        var option = new EmoteController.PlayEmoteOption
        {
            TargetId = target,
            DisableLogMessage = true,
            DisableSitDownAnimation = true,
        };
        option.VirtualTable = EmoteController.PlayEmoteOption.StaticVirtualTablePointer;
        var ok = chara->EmoteController.PlayEmote(emoteId, &option);
        logger.LogInformation("Emote sound restart for {who}: restarted emote {emote} from the start, result {ok}", who, emoteId, ok);
        return ok;
    }
}
