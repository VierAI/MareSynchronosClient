using MareSynchronos.API.Data;
using MareSynchronos.Services;
using Microsoft.Extensions.Logging;

namespace MareSynchronos.PlayerData.Pairs;

/// <summary>
/// 1.15.6.217 (VierAI): with "Auto-mute players playing blacklisted sounds" on, blacklisted .scd files (file name contains a
/// blacklisted word, NoiseFilter.Match) are removed from the pair's applied copy, so they are never downloaded or redirected and
/// even their first play is silent. A whitelisted file is kept (the whitelist wins, NoiseFilter.Match checks it first). A hand
/// unmute turns the blacklist off for that pair (NoiseAutoMute.IsManuallyOverridden), same as the per-path silencing. The
/// received data keeps every file, so turning auto-mute off or editing the lists restores them on the next reapply.
/// </summary>
public sealed partial class PairDataApplicationHandler
{
    private string _psyncStripSigPending = string.Empty; // guarded by _forkDebounceLock
    private string? _psyncStripSigApplied;               // guarded by _forkDebounceLock

    private bool PSyncBlacklistStripActive()
        => VierAIConfigStore.Current.NoisesAutoMuteBlacklisted
           && !MareSynchronos.UI.Components.NoiseAutoMute.IsManuallyOverridden(Pair.UserData.UID);

    private static bool PSyncIsBlacklistedScd(string gamePath)
        => gamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) && NoiseFilter.Match(gamePath) == NoiseFilterMatch.Blacklist;

    /// <summary>The blacklisted .scd paths that would be stripped from these paths right now ("" when stripping is off).</summary>
    private string PSyncStripSignatureFor(IEnumerable<string>? gamePaths)
    {
        if (gamePaths == null || !PSyncBlacklistStripActive()) return string.Empty;
        return string.Join('|', gamePaths.Where(PSyncIsBlacklistedScd).Select(p => p.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>Called with the incoming (unfiltered) data before it is filtered; part of the filter signature.</summary>
    private void PSyncNoteStripSignature(CharacterData received)
    {
        var sig = PSyncStripSignatureFor(received.FileReplacements.Values.SelectMany(l => l).SelectMany(r => r.GamePaths));
        lock (_forkDebounceLock) _psyncStripSigPending = sig;
    }

    /// <summary>True when a blacklist/whitelist edit or the auto-mute option changed which of this pair's sounds are stripped.</summary>
    public bool PSyncBlacklistStripOutdated()
    {
        string? applied;
        lock (_forkDebounceLock) applied = _psyncStripSigApplied;
        if (applied == null) return false;
        return !string.Equals(applied, PSyncStripSignatureFor(PSyncReceivedScdPaths), StringComparison.Ordinal);
    }

    /// <summary>Removes blacklisted .scd game paths from the (already cloned) data; an entry left without paths is dropped.</summary>
    private void PSyncStripBlacklistedSounds(CharacterData data)
    {
        if (!PSyncBlacklistStripActive()) return;
        var removed = 0;
        foreach (var kind in data.FileReplacements.Keys.ToList())
        {
            var list = data.FileReplacements[kind];
            if (!list.Any(f => f.GamePaths.Any(PSyncIsBlacklistedScd))) continue;
            var result = new List<FileReplacementData>(list.Count);
            foreach (var f in list)
            {
                if (!f.GamePaths.Any(PSyncIsBlacklistedScd)) { result.Add(f); continue; }
                var kept = f.GamePaths.Where(p => !PSyncIsBlacklistedScd(p)).ToArray();
                removed += f.GamePaths.Length - kept.Length;
                if (kept.Length == 0) continue;
                f.GamePaths = kept;
                result.Add(f);
            }
            data.FileReplacements[kind] = result;
        }
        if (removed > 0) Logger.LogDebug("{pair}: {count} blacklisted sound path(s) not applied (auto-mute blacklisted on)", Pair.UserData.AliasOrUID, removed);
    }
}
