using MareSynchronos.API.Data.Extensions;
using MareSynchronos.Services;
using Microsoft.Extensions.Logging;

namespace MareSynchronos.PlayerData.Pairs;

// 1.15.5.1704: mute for sounds the pair's object plays that are not their own synced .scd files (see PSyncForeignSounds).
public sealed partial class PairDataApplicationHandler
{
    private readonly HashSet<string> _psyncObservedForeignScd = new(StringComparer.OrdinalIgnoreCase); // framework thread only
    private string? _psyncMuteSignature; // framework thread only
    private int _psyncScanVersion;

    /// <summary>.scd game paths referenced inside the pair's applied .tmb/.pap/.avfx files (null until the first scan).</summary>
    public IReadOnlySet<string>? PSyncReferencedScdPaths { get; private set; }

    /// <summary>Game paths currently redirected to the mute target in this pair's collection (empty when not muted).</summary>
    public IReadOnlyCollection<string> PSyncMuteRedirectPaths { get; private set; } = [];

    private bool PSyncSoundsDisabled()
        => Pair.UserPair.OwnPermissions.IsDisableSounds() || Pair.UserPair.OtherPermissions.IsDisableSounds();

    /// <summary>1.15.6.206: blacklist auto-mute on and this .scd path matches the Noises blacklist.</summary>
    private static bool PSyncBlacklistSilenced(string gamePath)
        => VierAIConfigStore.Current.NoisesAutoMuteBlacklisted && NoiseFilter.Match(gamePath) == NoiseFilterMatch.Blacklist;

    private static void PSyncAddBlacklisted(SortedSet<string> into, IEnumerable<string>? paths)
    {
        if (paths == null) return;
        foreach (var p in paths) if (PSyncBlacklistSilenced(p)) into.Add(p);
    }

    /// <summary>A counted non-own .scd load on the pair's object (framework thread).</summary>
    public void PSyncObserveForeignSound(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || _psyncObservedForeignScd.Count >= PSyncForeignSounds.MaxObservedPerPair) return;
        if (!_psyncObservedForeignScd.Add(gamePath)) return;
        if (PSyncSoundsDisabled() || PSyncBlacklistSilenced(gamePath)) PSyncUpdateMuteRedirectsOnFramework();
    }

    /// <summary>After a mod-file apply: scan the applied .tmb/.pap/.avfx files in the background, then refresh the mute redirects.</summary>
    private void PSyncStartReferenceScan(IReadOnlyDictionary<string, string> applied, IReadOnlySet<string> swaps)
    {
        var files = applied
            .Where(kv => !swaps.Contains(kv.Key) && (kv.Key.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)
                || kv.Key.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) || kv.Key.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)))
            .Select(kv => kv.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var version = Interlocked.Increment(ref _psyncScanVersion);
        _ = PSyncForeignSounds.ScanReferencedScdAsync(files).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion || version != Volatile.Read(ref _psyncScanVersion)) return;
            PSyncReferencedScdPaths = t.Result;
            Logger.LogDebug("{pair}: {count} .scd paths referenced by {files} applied .tmb/.pap/.avfx files", Pair.UserData.AliasOrUID, t.Result.Count, files.Length);
            PSyncUpdateMuteRedirects();
        }, TaskScheduler.Default);
    }

    /// <summary>Sets or clears the mute redirects (any thread; runs on the framework thread).</summary>
    public void PSyncUpdateMuteRedirects()
    {
        if (_dalamudUtil.IsOnFrameworkThread) PSyncUpdateMuteRedirectsOnFramework();
        else _ = _dalamudUtil.RunOnFrameworkThread(() => { PSyncUpdateMuteRedirectsOnFramework(); });
    }

    /// <summary>1.15.6.207: on the framework thread, update now and return false if the Penumbra call failed (the caller may retry);
    /// off it, schedule the update and return true.</summary>
    public bool PSyncTryUpdateMuteRedirectsNow()
    {
        if (_dalamudUtil.IsOnFrameworkThread) return PSyncUpdateMuteRedirectsOnFramework();
        _ = _dalamudUtil.RunOnFrameworkThread(PSyncUpdateMuteRedirectsOnFramework);
        return true;
    }

    private bool PSyncUpdateMuteRedirectsOnFramework()
    {
        try
        {
            var coll = _penumbraCollection;
            if (coll == null) { _psyncMuteSignature = null; PSyncMuteRedirectPaths = []; return true; }

            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (PSyncSoundsDisabled())
            {
                if (PSyncReferencedScdPaths != null) paths.UnionWith(PSyncReferencedScdPaths);
                paths.UnionWith(_psyncObservedForeignScd);
                paths.Remove(PSyncForeignSounds.MuteTarget);
                // 1.15.6.210: a mute you set lets whitelisted sounds through; only their own "disable sounds" silences them.
                if (!Pair.UserPair.OtherPermissions.IsDisableSounds()) paths.RemoveWhere(NoiseFilter.IsWhitelisted);
            }
            else if (VierAIConfigStore.Current.NoisesAutoMuteBlacklisted
                && !MareSynchronos.UI.Components.NoiseAutoMute.IsManuallyOverridden(Pair.UserData.UID)) // 1.15.6.207: hand unmute = blacklist off for them
            {
                // 1.15.6.206: blacklisted sound files are silenced per path BEFORE they play (their own synced .scd, ones their
                // .tmb/.pap/.avfx reference, and observed VFX sounds). The whole-player auto-mute only reacts to the load, which
                // is the play itself, so a one-shot was always heard. A path seen for the first time is silenced from its next play.
                PSyncAddBlacklisted(paths, PSyncReceivedScdPaths);
                PSyncAddBlacklisted(paths, PSyncReferencedScdPaths);
                PSyncAddBlacklisted(paths, _psyncObservedForeignScd);
                paths.Remove(PSyncForeignSounds.MuteTarget);
            }

            var signature = coll.Value.ToString() + "|" + string.Join('|', paths);
            if (string.Equals(signature, _psyncMuteSignature, StringComparison.Ordinal)) return true;

            var redirects = paths.Count == 0 ? null : paths.ToDictionary(p => p, _ => PSyncForeignSounds.MuteTarget, StringComparer.OrdinalIgnoreCase);
            if (_ipcManager.Penumbra.SetMuteRedirectsOnFramework(Logger, coll.Value, Pair.UserData.UID, redirects))
            {
                _psyncMuteSignature = signature;
                PSyncMuteRedirectPaths = [.. paths];
                if (paths.Count > 0) Logger.LogDebug("{pair}: muting {count} non-own sound paths", Pair.UserData.AliasOrUID, paths.Count);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Mute redirect update failed for {pair}", Pair.UserData.AliasOrUID);
            return false;
        }
    }
}
