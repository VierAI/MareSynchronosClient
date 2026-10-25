using MareSynchronos.API.Data;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.FileCache;
using MareSynchronos.Utils;
using MareSynchronos.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MareSynchronos.PlayerData.Pairs;

// Fork additions (VierAI-fied) for the upstream data application handler. Hooks: validation read, end of a successful application.
public sealed partial class PairDataApplicationHandler
{
    private static readonly ConcurrentDictionary<string, byte> s_validationReadFailLogged = new(StringComparer.OrdinalIgnoreCase);

    // 1.15.5.1202 duplicate-apply debounce: a forced re-apply of the exact same data hash within this window of the last
    // finished apply is skipped (suspected Glamourer/Penumbra redraw loop on Mac). Never skips changed data, a pending
    // job-change redraw, or the first apply after the pair left view (UpdateVisibility(false) resets it).
    private const long ForkDuplicateApplyWindowMs = 3000;
    private readonly object _forkDebounceLock = new();
    private string? _forkLastAppliedHash;
    private long _forkLastAppliedTicks;

    // Frame-time audit item 1: split apply. Only one player's Penumbra stage runs per frame (shared by all pairs). The gate is
    // held for one extra framework tick after the stage so the next player's Penumbra change lands on a later frame.
    private static readonly SemaphoreSlim s_forkPenumbraFrameGate = new(1, 1);

    /// <summary>
    /// Runs the change groups (Penumbra stage, Glamourer, the rest, redraw) in separate framework calls, in order. New data
    /// for this pair cancels the token, which stops before the next stage; the redraw stage is last, so a partly applied
    /// apply is never redrawn. Returns the same result the single-frame path would.
    /// </summary>
    private async Task<bool> ForkApplyChangeGroupsSplitAsync(PlayerChanges[][] changeGroup, bool needsPenumbraStage,
        Func<PlayerChanges[], bool?> runGroup, CancellationToken cancellationToken)
    {
        // Audit fix: RunOnFrameworkThread runs inline when the continuation is already on the framework thread, so two
        // groups could land in the same frame. Every group after the first now waits for a real framework tick first
        // (IFramework.DelayTicks), and the Penumbra gate is held across one real tick.
        int stagesDone = 0;
        try
        {
            for (int i = 0; i < changeGroup.Length; i++)
            {
                var changes = changeGroup[i];
                bool penumbraStage = i == 0;
                if (changes.Length == 0 && !(penumbraStage && needsPenumbraStage))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (stagesDone > 0)
                {
                    await _dalamudUtil.WaitFrameworkTicksAsync(1, cancellationToken).ConfigureAwait(false);
                }

                bool? result;
                if (penumbraStage)
                {
                    await s_forkPenumbraFrameGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        result = await _dalamudUtil.RunOnFrameworkThread(() => runGroup(changes)).ConfigureAwait(false);
                        // hold the gate through one more real framework tick so the next player's Penumbra change lands later
                        await _dalamudUtil.WaitFrameworkTicksAsync(1, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        s_forkPenumbraFrameGate.Release();
                    }
                }
                else
                {
                    result = await _dalamudUtil.RunOnFrameworkThread(() => runGroup(changes)).ConfigureAwait(false);
                }

                stagesDone++;
                if (result.HasValue)
                {
                    return result.Value;
                }
            }
        }
        catch (OperationCanceledException) when (stagesDone > 0)
        {
            // By design (audit item 4): a cancel between groups can leave the earlier groups applied (for example Penumbra
            // mods without the Glamourer state) until the newer apply that caused the cancel runs. No redraw has happened
            // yet, so Penumbra's changes are not shown; Glamourer's are. The newer apply redoes every group.
            Logger.LogDebug("Split apply for {pair} cancelled after {stages} group(s); the newer apply will redo them", Pair.PairUIDName, stagesDone);
            throw;
        }

        return true;
    }

    private void ForkRecordApplied(CharacterData charaData)
    {
        lock (_forkDebounceLock)
        {
            _forkLastAppliedHash = charaData.DataHash.Value;
            _forkLastAppliedTicks = Environment.TickCount64;
        }
    }

    // Mute/unmute fix: the data as last applied (after the sound/animation/VFX filter). The cache keeps the unfiltered data.
    private CharacterData? _forkAppliedData;
    private int _forkBypassDebounceOnce;

    /// <summary>User clicked Reapply Last Data: the next apply always runs (no duplicate-apply debounce).</summary>
    public void ForkBypassDebounceOnce() { Interlocked.Exchange(ref _forkBypassDebounceOnce, 1); Interlocked.Exchange(ref _forkUserReapplyPending, 1); }

    // Reapply Last Data clicked: the next sound restart is a full stop and replay (not just a replay of the running emote).
    private int _forkUserReapplyPending;

    private void ForkSetApplied(CharacterData? applied)
    {
        var copy = applied?.DeepClone();
        lock (_forkDebounceLock)
        {
            _forkAppliedData = copy;
            _forkAppliedSig = applied == null ? null : _forkValidateSig;
        }
    }

    /// <summary>The cached data with the file list that was actually applied (same hash only), for the diff.</summary>
    private CharacterData? ForkAppliedBaseline(CharacterData? cached)
    {
        CharacterData? applied;
        lock (_forkDebounceLock) applied = _forkAppliedData;
        if (cached == null || applied == null || !string.Equals(cached.DataHash.Value, applied.DataHash.Value, StringComparison.Ordinal)) return cached;
        cached.FileReplacements = applied.DeepClone().FileReplacements;
        return cached;
    }

    // 1.15.5.1204: mute/unmute detection is by the filter settings (permissions + filter options) the data was applied with,
    // never by file counts, so ordinary applies (file drops, validation) can't look like an unmute.
    private string? _forkAppliedSig;
    private string _forkValidateSig = string.Empty;
    private int _forkSoundRestorePending;

    private string ForkFilterSignature()
    {
        var own = Pair.UserPair.OwnPermissions;
        var other = Pair.UserPair.OtherPermissions;
        var c = _configService.Current;
        return string.Join('|', own.IsDisableSounds(), own.IsDisableAnimations(), own.IsDisableVFX(),
            other.IsDisableSounds(), other.IsDisableAnimations(), other.IsDisableVFX(),
            c.FilterSounds, c.FilterAnimations, c.FilterVfx, c.FilterMinionsAndMounts, c.FilterPets, c.DoFilteringBidirectionDirectPairs,
            Pair.UserPair.IndividualPairStatus, c.UIDsToOverrideFilter.Count);
    }

    /// <summary>Same data hash, but applied under different filter settings (mute/unmute and similar): must re-apply.</summary>
    private bool ForkFilterChanged(CharacterData filtered, CharacterData? baseline)
    {
        var sig = ForkFilterSignature();
        string? appliedSig;
        lock (_forkDebounceLock)
        {
            _forkValidateSig = sig;
            appliedSig = _forkAppliedSig;
        }
        var changed = baseline != null && appliedSig != null
            && string.Equals(filtered.DataHash.Value, baseline.DataHash.Value, StringComparison.Ordinal)
            && !string.Equals(sig, appliedSig, StringComparison.Ordinal);
        // An unmute restore: the next finished apply may mark the player noisy again (only if they were noisy when muted).
        Interlocked.Exchange(ref _forkSoundRestorePending, changed && SoundActivity.ContainsSound(filtered) ? 1 : 0);
        return changed;
    }

    private void ForkResetDebounce()
    {
        lock (_forkDebounceLock) _forkLastAppliedHash = null;
    }

    private bool ForkIsDuplicateApply(string newHash, string? cachedHash, bool force)
    {
        if (Interlocked.Exchange(ref _forkBypassDebounceOnce, 0) == 1) return false;
        if (!force || string.IsNullOrEmpty(newHash) || !string.Equals(newHash, cachedHash, StringComparison.Ordinal)) return false;
        if (Volatile.Read(ref _redrawOnNextApplication) != 0) return false;
        lock (_forkDebounceLock)
        {
            return string.Equals(_forkLastAppliedHash, newHash, StringComparison.Ordinal)
                && Environment.TickCount64 - _forkLastAppliedTicks < ForkDuplicateApplyWindowMs;
        }
    }

    /// <summary>
    /// Reads a file for validation with low I/O priority and one retry (Wine file I/O fix, 1.15.5.910).
    /// Returns null if it can't be read; the caller drops the file under Wine or when it is missing (fail closed), otherwise uses it unchecked.
    /// </summary>
    private async Task<byte[]?> ReadForValidationAsync(string hash, string filePath, CancellationToken token)
    {
        Exception? failure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await LowPriorityFile.ReadAllBytesAsync(filePath, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex;
                if (attempt == 0) await Task.Delay(250, token).ConfigureAwait(false);
            }
        }

        var gone = failure is FileNotFoundException or DirectoryNotFoundException;
        var drop = LowPriorityFile.IsWine || gone;
        if (s_validationReadFailLogged.TryAdd(hash, 0))
        {
            Logger.LogWarning(failure, "Couldn't read downloaded file {filePath} for validation ({kind}); {action}", filePath,
                gone ? "file missing" : "read error", drop ? "skipping this file for the pair" : "using it anyway");
        }
        return null;
    }

    /// <summary>Sound activity state and the emote sound restart after a redraw (fork features).</summary>
    private void OnForkDataApplied(Guid applicationId, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData)
    {
        ForkRecordApplied(charaData);
        var soundRestore = Interlocked.Exchange(ref _forkSoundRestorePending, 0) == 1;
        // Full restart only when the user asked for the sound back (unmute or Reapply Last Data); an ordinary data update
        // (a DJ changing songs every few minutes) keeps the plain replay so the dance is not cut off each time.
        var fullRestart = Interlocked.Exchange(ref _forkUserReapplyPending, 0) == 1 || soundRestore;
        if (SoundActivity.OnDataApplied(Pair, charaData)) Mediator.Publish(new PairSoundActiveChangedMessage(Pair));

        var redrawn = updatedData.TryGetValue(ObjectKind.Player, out var changes) && changes.Contains(PlayerChanges.ForcedRedraw);
        if ((!redrawn && !fullRestart) || !SoundActivity.ContainsSound(charaData)) return;

        var contentId = Pair.ContendId;
        var who = Pair.UserData.AliasOrUID;
        _ = Task.Run(async () =>
        {
            try
            {
                // A redraw leaves a looping emote running without its sound; replay the emote once the model settled.
                await Task.Delay(EmoteSoundRestart.SettleDelayMs).ConfigureAwait(false);
                bool replayed;
                if (fullRestart)
                {
                    // Stop, let the game drop the running timeline (and the VFX that carried the sound), then play from the start.
                    ulong target = 0;
                    var emoteId = await _dalamudUtil.RunOnFrameworkThread(() =>
                    {
                        if (!_dalamudUtil.TryGetPlayerAddressAndIndexOnFrameworkByContentId(contentId, out var address, out _)) return (ushort)0;
                        return _dalamudUtil.IsLiveObject(address) ? EmoteSoundRestart.TryStopLoopingEmote(Logger, address, who, out target) : (ushort)0;
                    }).ConfigureAwait(false);
                    if (emoteId != 0) await Task.Delay(EmoteSoundRestart.StopToPlayDelayMs).ConfigureAwait(false);
                    replayed = emoteId != 0 && await _dalamudUtil.RunOnFrameworkThread(() =>
                    {
                        if (!_dalamudUtil.TryGetPlayerAddressAndIndexOnFrameworkByContentId(contentId, out var address, out _)) return false;
                        return _dalamudUtil.IsLiveObject(address) && EmoteSoundRestart.PlayEmoteFromStart(Logger, address, who, emoteId, target);
                    }).ConfigureAwait(false);
                }
                else
                {
                    replayed = await _dalamudUtil.RunOnFrameworkThread(() =>
                    {
                        if (!_dalamudUtil.TryGetPlayerAddressAndIndexOnFrameworkByContentId(contentId, out var address, out _)) return false;
                        return _dalamudUtil.IsLiveObject(address) && EmoteSoundRestart.TryReplayEmote(Logger, address, who);
                    }).ConfigureAwait(false);
                }
                // Unmute fix: count the replayed sound right away (Penumbra may not report a new .scd load for an already loaded file),
                // so the Noises list shows the player as making noise again without waiting for a new sound file.
                // 1.15.5.1204: only for an unmute restore of a player who was noisy when muted; any other replay waits for a real .scd load.
                // Next build: also when the emote could not be restarted (sound not carried by a looping emote, e.g. a DJ setup) and when
                // the unmute came through the full user-style reapply; the noisy-at-mute flag alone marks it as an unmute restore.
                if ((replayed || soundRestore || fullRestart) && SoundActivity.TakeNoisyAtMute(Pair) && SoundActivity.OnSoundLoaded(Pair, DateTimeOffset.UtcNow, sustained: true))
                    Mediator.Publish(new PairSoundActiveChangedMessage(Pair));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{applicationId}] Emote sound restart failed", applicationId);
            }
        });
    }
}
