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

    /// <summary>A counted non-own .scd load on the pair's object (framework thread).</summary>
    public void PSyncObserveForeignSound(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || _psyncObservedForeignScd.Count >= PSyncForeignSounds.MaxObservedPerPair) return;
        if (!_psyncObservedForeignScd.Add(gamePath)) return;
        if (PSyncSoundsDisabled()) PSyncUpdateMuteRedirectsOnFramework();
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
        else _ = _dalamudUtil.RunOnFrameworkThread(PSyncUpdateMuteRedirectsOnFramework);
    }

    private void PSyncUpdateMuteRedirectsOnFramework()
    {
        try
        {
            var coll = _penumbraCollection;
            if (coll == null) { _psyncMuteSignature = null; PSyncMuteRedirectPaths = []; return; }

            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (PSyncSoundsDisabled())
            {
                if (PSyncReferencedScdPaths != null) paths.UnionWith(PSyncReferencedScdPaths);
                paths.UnionWith(_psyncObservedForeignScd);
                paths.Remove(PSyncForeignSounds.MuteTarget);
            }

            var signature = coll.Value.ToString() + "|" + string.Join('|', paths);
            if (string.Equals(signature, _psyncMuteSignature, StringComparison.Ordinal)) return;

            var redirects = paths.Count == 0 ? null : paths.ToDictionary(p => p, _ => PSyncForeignSounds.MuteTarget, StringComparer.OrdinalIgnoreCase);
            if (_ipcManager.Penumbra.SetMuteRedirectsOnFramework(Logger, coll.Value, Pair.UserData.UID, redirects))
            {
                _psyncMuteSignature = signature;
                PSyncMuteRedirectPaths = [.. paths];
                if (paths.Count > 0) Logger.LogDebug("{pair}: muting {count} non-own sound paths", Pair.UserData.AliasOrUID, paths.Count);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Mute redirect update failed for {pair}", Pair.UserData.AliasOrUID);
        }
    }
}
