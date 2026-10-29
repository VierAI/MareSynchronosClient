using MareSynchronos.MareConfiguration;
using MareSynchronos.MareConfiguration.Configurations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MareSynchronos.Services;

/// <summary>
/// Fork-only settings (not present in the official plugin). They live in their own file, <c>vierai-config.json</c>,
/// next to the shared config files, so the official plugin never sees them and can't drop them when it rewrites
/// <c>config.json</c> / <c>playerperformance.json</c>. Swapping between the official plugin and this fork keeps them.
/// </summary>
public class VierAIConfig
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // Sound activity / name tags / Noise makers window (were in config.json)
    public bool NamePlatePairedSoundIndicator { get; set; } = false;
    public bool ShowNoiseMakersWindow { get; set; } = false;

    // Seconds a noise entry takes to fade out and drop from the Noises window / folder (10-300).
    public int NoisesFadeSeconds { get; set; } = 60;

    // Seconds a muted row stays in the Noises window after the mouse last hovered it (5-300).
    public int NoisesMutedHideSeconds { get; set; } = 15;


    // 1.15.5.1210: Auto sync nearby animations (AutoAnimationSync): restart both looping emotes when a nearby pair and you
    // are both in one. Radius in metres (0.1-3). The intro replay is a separate experimental switch.
    public bool AutoSyncNearbyAnimations { get; set; } = false;
    public float AutoSyncNearbyRadius { get; set; } = 1.0f;
    public bool AutoSyncIntroReplay { get; set; } = false;

    // Main window: show a green "(N)" next to each syncshell = members you reach through no other syncshell or direct pair.
    public bool ShowSyncshellUniqueCount { get; set; } = true;

    // 1.15.5.1216: let local debugging tools read visible pair info over the PSync.GetPairStates IPC. Off = it returns an empty array.
    public bool AllowLocalDebugPairInfo { get; set; } = false;

    // Wine only: the user dismissed the "cache folder may be cloud-synced" warning in Settings (see CacheFolderHealthCheck).
    public bool CacheFolderWarningDismissed { get; set; } = false;

    // Wine only: skip the Havok animation/skeleton bone check on outgoing data (see WineAnalysisSetting). Off = check runs.
    public bool WineSkipAnimationAnalysis { get; set; } = false;

    // Frame-time: run a pair's data apply over several frames (Penumbra, then Glamourer, then the rest, redraw last) and allow
    // at most one player's Penumbra change per frame. Off = the whole apply runs in one frame (upstream behavior).
    public bool SplitApplyAcrossFrames { get; set; } = true;

    // After a redraw, wait for the skeleton (max ~3s) and apply Customize+ scale and Heels once more. Off = upstream behavior.
    public bool ReapplyScaleAfterRedraw { get; set; } = true;

    // Download priority (were in playerperformance.json)
    // Priority tiers (schema 3): tier 0 = UIDsToPrioritize, tier 1 = in-game friends, tier 2 = party members, tier 3 = direct syncs, tier 4 = everyone else. All always on;
    // free download slots go to the best waiting tier. Old fields (PrioritizeFriends, PrioritizeInGameFriends, PrioritizeDirectSyncs,
    // PriorityReservedSlots, NonPriorityDownloadMode, NonPriorityLimitMBps) are ignored on load and dropped on the next save.
    public List<string> UIDsToPrioritize { get; set; } = new();

    // Texture size cap: shrink textures that are only drawn on physically small models (rings, earrings, ...). The size comes
    // from the model bounding box (1.0 = 1 metre); a texture uses the LARGEST model it is on. Shrunk copies are made in the
    // background by dropping mip levels (no recompression) into <cache>/downscaled and used from the next apply.
    public bool TextureCapEnabled { get; set; } = false;
    public float TextureCapTinyMaxCm { get; set; } = 1f;
    public int TextureCapTinyPx { get; set; } = 512;
    public float TextureCapSmallMaxCm { get; set; } = 5f;
    public int TextureCapSmallPx { get; set; } = 1024;
    public float TextureCapMediumMaxCm { get; set; } = 20f;
    public int TextureCapMediumPx { get; set; } = 2048;
    public int TextureCapEyeFloorPx { get; set; } = 2048;
    public int TextureCapWorkerThreads { get; set; } = 1;
    public List<string> TextureCapExemptUIDs { get; set; } = new();

    // 1.15.5.1217: memory/VRAM budget (off by default = unchanged behaviour), blocklist, missing-file auto retry.
    public bool MemoryBudgetEnabled { get; set; } = false;
    /// <summary>0 = Manual MB cap, 1 = Auto (live GPU memory via DXGI; falls back to Manual on iGPU/Wine/failure).</summary>
    public int MemoryBudgetMode { get; set; } = 0;
    public int MemoryBudgetManualMiB { get; set; } = 6144;
    public int MemoryBudgetCrowdedMiB { get; set; } = 3072;
    public int MemoryBudgetCrowdedPlayers { get; set; } = 40;
    public int MemoryBudgetHeadroomPercent { get; set; } = 10;
    public int MemoryBudgetHeadroomMinMiB { get; set; } = 512;
    public int MemoryBudgetMarginPercent { get; set; } = 10;
    public int MemoryBudgetMinHoldSeconds { get; set; } = 30;
    /// <summary>Unused since the perf-warning row layout (kept so older files load cleanly).</summary>
    public bool ShowPairPerfInline { get; set; } = true;
    /// <summary>Visible section: show the metric(s) over your performance warning limits next to the pair (e.g. "277M").</summary>
    public bool PerfWarningInVisiblePane { get; set; } = true;
    /// <summary>Print a one-time chat line per pair per session when a visible pair is first over a warning limit (rate-limited).</summary>
    public bool PerfWarningInChat { get; set; } = false;
    public bool AutoRedownloadMissingFiles { get; set; } = true;
    public List<string> BlockedUIDs { get; set; } = new();
    /// <summary>1.15.5.1220: UIDs the memory budget never holds (like tier 0).</summary>
    public List<string> MemoryBudgetExemptUIDs { get; set; } = new();

    /// <summary>1.15.5.1411: temporary automatic threshold pauses (height/VRAM/triangles with a timeout), so after a game
    /// restart the right-click override can still find the paused player until the pause expires. Expired entries are
    /// pruned on load and by the auto-unpause check.</summary>
    public List<VierAITempPause> TempPauses { get; set; } = new();
}

/// <summary>1.15.5.1411: one temporary auto-pause: UID, PauseReason name, last known hashed content id and character name,
/// and the expiry (same time as the PendingPausedPairs entry the auto-unpause timer uses).</summary>
public sealed class VierAITempPause
{
    public string Uid { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string HashedCid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset ExpiresUtc { get; set; }
}

/// <summary>1.15.5.1411: add/remove/prune/find helpers for <see cref="VierAIConfig.TempPauses"/>; every change saves.</summary>
public static class VierAITempPauses
{
    private static readonly object Lock = new();

    public static void Upsert(string uid, string reason, string hashedCid, string name, DateTimeOffset expiresUtc)
    {
        if (string.IsNullOrEmpty(uid)) return;
        lock (Lock)
        {
            var list = VierAIConfigStore.Current.TempPauses;
            list.RemoveAll(e => string.Equals(e.Uid, uid, StringComparison.Ordinal));
            list.Add(new VierAITempPause { Uid = uid, Reason = reason, HashedCid = hashedCid ?? string.Empty, Name = name ?? string.Empty, ExpiresUtc = expiresUtc });
        }
        Save();
    }

    public static void Remove(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        int removed;
        lock (Lock) removed = VierAIConfigStore.Current.TempPauses.RemoveAll(e => string.Equals(e.Uid, uid, StringComparison.Ordinal));
        if (removed > 0) Save();
    }

    public static int PruneExpired()
    {
        int removed;
        var now = DateTimeOffset.UtcNow;
        lock (Lock) removed = VierAIConfigStore.Current.TempPauses.RemoveAll(e => e.ExpiresUtc <= now);
        if (removed > 0) Save();
        return removed;
    }

    /// <summary>UID of an unexpired entry matching one of the hashed content ids, else a unique match on the name.</summary>
    public static string? FindUid(IEnumerable<string> hashedCids, string? name)
    {
        var now = DateTimeOffset.UtcNow;
        lock (Lock)
        {
            var live = VierAIConfigStore.Current.TempPauses.Where(e => e.ExpiresUtc > now).ToList();
            foreach (var h in hashedCids)
            {
                var hit = live.FirstOrDefault(e => !string.IsNullOrEmpty(e.HashedCid) && string.Equals(e.HashedCid, h, StringComparison.Ordinal));
                if (hit != null) return hit.Uid;
            }
            if (string.IsNullOrEmpty(name)) return null;
            var byName = live.Where(e => string.Equals(e.Name, name, StringComparison.Ordinal)).ToList();
            return byName.Count == 1 ? byName[0].Uid : null;
        }
    }

    private static void Save()
    {
        VierAIConfigStore.MarkDirty();
        VierAIConfigStore.TrySave(out _);
    }
}

/// <summary>A log line collected before the logger exists (emitted by <see cref="VierAIConfigService"/> on start).</summary>
public sealed record VierAIConfigLogLine(bool Warning, string Text);

/// <summary>
/// Static holder for <see cref="VierAIConfig"/>. Static so the old config properties can pass through to it without
/// needing a service reference. <see cref="Initialize"/> is called once from the plugin constructor, before any
/// shared config file can be rewritten, so the one-time migration still sees the old fields.
/// </summary>
public static class VierAIConfigStore
{
    public const string FileName = "vierai-config.json";
    public const string BadFileSuffix = ".bad";

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static VierAIConfig _current = new();
    private static string _directory = string.Empty;
    private static string _lastWrittenJson = string.Empty;
    private static volatile bool _dirty;

    public static VierAIConfig Current => _current;
    public static bool IsDirty => _dirty;
    public static string FilePath => Path.Combine(_directory, FileName);
    public static List<VierAIConfigLogLine> StartupLog { get; } = new();

    public static void MarkDirty() => _dirty = true;

    /// <summary>Loads vierai-config.json, or migrates from the old shared files when it doesn't exist yet.</summary>
    public static void Initialize(string configDirectory)
    {
        lock (Lock)
        {
            _directory = configDirectory;
            _current = new VierAIConfig();
            _lastWrittenJson = string.Empty;
            _dirty = false;
            StartupLog.Clear();
            try
            {
                var notPersisted = RoundTripSelfCheck();
                if (notPersisted.Count > 0) StartupLog.Add(new(true, $"{FileName} self-check: these settings are not reloaded after save: {string.Join(", ", notPersisted)}"));
                else StartupLog.Add(new(false, $"{FileName} self-check: all settings survive save/reload"));
            }
            catch (Exception ex) { StartupLog.Add(new(true, $"{FileName} self-check failed to run: {ex.Message}")); }

            var path = FilePath;
            if (File.Exists(path))
            {
                if (!TryLoadFile(path, _current, out var error))
                {
                    var bad = path + BadFileSuffix;
                    try
                    {
                        File.Move(path, bad, true);
                        StartupLog.Add(new(true, $"{FileName} could not be read ({error}); moved to {Path.GetFileName(bad)} and using defaults"));
                    }
                    catch (Exception ex)
                    {
                        StartupLog.Add(new(true, $"{FileName} could not be read ({error}) and could not be backed up ({ex.Message}); using defaults"));
                    }
                    _current = new VierAIConfig();
                    _dirty = true;
                }
                else
                {
                    _lastWrittenJson = JsonSerializer.Serialize(_current, WriteOptions);
                    UpgradeSchemaLocked();
                }
            }
            else
            {
                MigrateFromSharedConfigs();
                UpgradeSchemaLocked();
                _dirty = true;
            }

            if (_dirty) TrySaveLocked(out _);
        }
    }

    /// <summary>Writes the file if something changed. Returns false (and the error text) if the write failed.</summary>
    public static bool TrySave(out string? error)
    {
        lock (Lock) return TrySaveLocked(out error);
    }

    private static bool TrySaveLocked(out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(_directory)) { _dirty = false; return true; }
        _dirty = false;
        try
        {
            var json = JsonSerializer.Serialize(_current, WriteOptions);
            if (json == _lastWrittenJson && File.Exists(FilePath)) return true;
            Directory.CreateDirectory(_directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, true);
            _lastWrittenJson = json;
            return true;
        }
        catch (Exception ex)
        {
            _dirty = true; // retry on the next tick (e.g. list modified while serializing, file locked)
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Schema 1 -> 2: the old "Prioritize friends" meant directly paired players, which is now the "direct syncs" tier.
    /// In-game friends start off (it is a new option). The same applies to the old playerperformance.json value.
    /// </summary>
    private static void UpgradeSchemaLocked()
    {
        var c = _current;
        if (c.SchemaVersion < VierAIConfig.CurrentSchemaVersion)
        {
            StartupLog.Add(new(false, $"{FileName} upgraded from schema {c.SchemaVersion} to {VierAIConfig.CurrentSchemaVersion}"));
            c.SchemaVersion = VierAIConfig.CurrentSchemaVersion;
            _dirty = true;
        }
    }

    private static void MigrateFromSharedConfigs()
    {
        var found = new List<string>();
        var probe = new VierAIConfig();
        ReadOldFile(MareConfigService.ConfigName, probe, ["NamePlatePairedSoundIndicator", "ShowNoiseMakersWindow"], found);
        ReadOldFile(PlayerPerformanceConfigService.ConfigName, probe,
            ["UIDsToPrioritize"], found);
        _current = probe;
        StartupLog.Add(new(false, found.Count == 0
            ? $"No {FileName} yet and no old fork-only settings found in {MareConfigService.ConfigName}/{PlayerPerformanceConfigService.ConfigName}; created {FileName} with defaults"
            : $"Migrated fork-only settings to {FileName} (old files left untouched): {string.Join(", ", found)}"));
    }

    private static void ReadOldFile(string fileName, VierAIConfig target, string[] fields, List<string> found)
    {
        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var temp = new VierAIConfig();
            ApplyFields(doc.RootElement, temp, fields, null);
            foreach (var f in fields)
            {
                if (!doc.RootElement.TryGetProperty(f, out _)) continue;
                CopyField(temp, target, f);
                found.Add(f == "UIDsToPrioritize" ? $"{f}({temp.UIDsToPrioritize.Count})" : $"{f}={GetField(temp, f)}");
            }
            return;
        }
        catch (Exception ex)
        {
            StartupLog.Add(new(true, $"Could not read old settings from {fileName} for migration: {ex.Message}"));
        }
    }

    /// <summary>
    /// 1.15.5.1218 save/reload self-check (no test project in this repo): for every settable property, change only that one
    /// to a valid non-default value, serialize it like Save does and load it back through ApplyFields. Any property that
    /// comes back at its default is not being loaded. Returns the names that failed (empty = all persist).
    /// </summary>
    public static List<string> RoundTripSelfCheck()
    {
        var failed = new List<string>();
        foreach (var prop in typeof(VierAIConfig).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanWrite || !prop.CanRead || prop.Name == nameof(VierAIConfig.SchemaVersion)) continue;
            var src = new VierAIConfig();
            object? changed = prop.GetValue(src) switch
            {
                bool b => !b,
                int i => i + 1,
                float f => f + 0.05f,
                List<string> => new List<string> { "SELFCHECK1" },
                _ => null,
            };
            if (changed == null) continue;
            prop.SetValue(src, changed);
            var dst = new VierAIConfig();
            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(src, WriteOptions));
                ApplyFields(doc.RootElement, dst, null, null);
            }
            catch { failed.Add(prop.Name); continue; }
            var got = prop.GetValue(dst);
            var ok = changed is List<string> l1 && got is List<string> l2 ? l1.SequenceEqual(l2) : Equals(changed, got);
            if (!ok) failed.Add(prop.Name);
        }
        return failed;
    }

    private static bool TryLoadFile(string path, VierAIConfig target, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "not a JSON object";
                return false;
            }
            var bad = new List<string>();
            ApplyFields(doc.RootElement, target, null, bad);
            foreach (var f in bad) StartupLog.Add(new(true, $"{FileName}: invalid value for {f}, using default"));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Applies each known field on its own, so one bad value never loses the others. Unknown fields are ignored.</summary>
    private static void ApplyFields(JsonElement root, VierAIConfig target, string[]? only, List<string>? invalid)
    {
        void Try(string name, Action<JsonElement> apply)
        {
            if (only != null && Array.IndexOf(only, name) < 0) return;
            if (!root.TryGetProperty(name, out var el)) return;
            try { apply(el); }
            catch { invalid?.Add(name); }
        }

        Try(nameof(VierAIConfig.SchemaVersion), el => target.SchemaVersion = el.GetInt32());
        Try(nameof(VierAIConfig.NamePlatePairedSoundIndicator), el => target.NamePlatePairedSoundIndicator = el.GetBoolean());
        Try(nameof(VierAIConfig.ShowNoiseMakersWindow), el => target.ShowNoiseMakersWindow = el.GetBoolean());
        Try(nameof(VierAIConfig.CacheFolderWarningDismissed), el => target.CacheFolderWarningDismissed = el.GetBoolean());
        Try(nameof(VierAIConfig.WineSkipAnimationAnalysis), el => target.WineSkipAnimationAnalysis = el.GetBoolean());
        Try(nameof(VierAIConfig.SplitApplyAcrossFrames), el => target.SplitApplyAcrossFrames = el.GetBoolean());
        Try(nameof(VierAIConfig.ReapplyScaleAfterRedraw), el => target.ReapplyScaleAfterRedraw = el.GetBoolean());
        Try(nameof(VierAIConfig.ShowSyncshellUniqueCount), el => target.ShowSyncshellUniqueCount = el.GetBoolean());
        Try(nameof(VierAIConfig.NoisesMutedHideSeconds), el => target.NoisesMutedHideSeconds = Math.Clamp(el.GetInt32(), 5, 60));
        Try(nameof(VierAIConfig.AutoSyncNearbyAnimations), el => target.AutoSyncNearbyAnimations = el.GetBoolean());
        Try(nameof(VierAIConfig.AutoSyncNearbyRadius), el => target.AutoSyncNearbyRadius = Math.Clamp(el.GetSingle(), 0.1f, 3f));
        Try(nameof(VierAIConfig.AutoSyncIntroReplay), el => target.AutoSyncIntroReplay = el.GetBoolean());
        Try(nameof(VierAIConfig.NoisesFadeSeconds), el => target.NoisesFadeSeconds = Math.Clamp(el.GetInt32(), 10, 300));
        Try(nameof(VierAIConfig.TextureCapEnabled), el => target.TextureCapEnabled = el.GetBoolean());
        Try(nameof(VierAIConfig.TextureCapTinyMaxCm), el => target.TextureCapTinyMaxCm = el.GetSingle());
        Try(nameof(VierAIConfig.TextureCapTinyPx), el => target.TextureCapTinyPx = el.GetInt32());
        Try(nameof(VierAIConfig.TextureCapSmallMaxCm), el => target.TextureCapSmallMaxCm = el.GetSingle());
        Try(nameof(VierAIConfig.TextureCapSmallPx), el => target.TextureCapSmallPx = el.GetInt32());
        Try(nameof(VierAIConfig.TextureCapMediumMaxCm), el => target.TextureCapMediumMaxCm = el.GetSingle());
        Try(nameof(VierAIConfig.TextureCapMediumPx), el => target.TextureCapMediumPx = el.GetInt32());
        Try(nameof(VierAIConfig.TextureCapEyeFloorPx), el => target.TextureCapEyeFloorPx = el.GetInt32());
        Try(nameof(VierAIConfig.TextureCapWorkerThreads), el => target.TextureCapWorkerThreads = Math.Clamp(el.GetInt32(), 1, 2));
        Try(nameof(VierAIConfig.TextureCapExemptUIDs), el =>
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            target.TextureCapExemptUIDs = list;
        });
        Try(nameof(VierAIConfig.AllowLocalDebugPairInfo), el => target.AllowLocalDebugPairInfo = el.GetBoolean());
        // 1.15.5.1218: budget/blocklist fields (1217 wrote them but never read them back). Clamps match the settings UI.
        Try(nameof(VierAIConfig.MemoryBudgetEnabled), el => target.MemoryBudgetEnabled = el.GetBoolean());
        Try(nameof(VierAIConfig.MemoryBudgetMode), el => target.MemoryBudgetMode = Math.Clamp(el.GetInt32(), 0, 1));
        Try(nameof(VierAIConfig.MemoryBudgetManualMiB), el => target.MemoryBudgetManualMiB = Math.Clamp(el.GetInt32(), 256, 262144));
        Try(nameof(VierAIConfig.MemoryBudgetCrowdedMiB), el => target.MemoryBudgetCrowdedMiB = Math.Clamp(el.GetInt32(), 0, 262144));
        Try(nameof(VierAIConfig.MemoryBudgetCrowdedPlayers), el => target.MemoryBudgetCrowdedPlayers = Math.Clamp(el.GetInt32(), 0, 1000));
        Try(nameof(VierAIConfig.MemoryBudgetHeadroomPercent), el => target.MemoryBudgetHeadroomPercent = Math.Clamp(el.GetInt32(), 0, 50));
        Try(nameof(VierAIConfig.MemoryBudgetHeadroomMinMiB), el => target.MemoryBudgetHeadroomMinMiB = Math.Clamp(el.GetInt32(), 0, 16384));
        Try(nameof(VierAIConfig.MemoryBudgetMarginPercent), el => target.MemoryBudgetMarginPercent = Math.Clamp(el.GetInt32(), 0, 50));
        Try(nameof(VierAIConfig.MemoryBudgetMinHoldSeconds), el => target.MemoryBudgetMinHoldSeconds = Math.Clamp(el.GetInt32(), 10, 600));
        Try(nameof(VierAIConfig.ShowPairPerfInline), el => target.ShowPairPerfInline = el.GetBoolean());
        Try(nameof(VierAIConfig.PerfWarningInVisiblePane), el => target.PerfWarningInVisiblePane = el.GetBoolean());
        Try(nameof(VierAIConfig.PerfWarningInChat), el => target.PerfWarningInChat = el.GetBoolean());
        Try(nameof(VierAIConfig.AutoRedownloadMissingFiles), el => target.AutoRedownloadMissingFiles = el.GetBoolean());
        Try(nameof(VierAIConfig.MemoryBudgetExemptUIDs), el =>
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                var s = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s) && s.Length <= 64 && !list.Contains(s, StringComparer.Ordinal) && list.Count < 5000) list.Add(s);
            }
            target.MemoryBudgetExemptUIDs = list;
        });
        Try(nameof(VierAIConfig.TempPauses), el =>
        {
            // 1.15.5.1411: expired entries are pruned on load; one entry per UID.
            var now = DateTimeOffset.UtcNow;
            var list = new List<VierAITempPause>();
            foreach (var item in el.EnumerateArray())
            {
                VierAITempPause? e;
                try { e = item.Deserialize<VierAITempPause>(); } catch { continue; }
                if (e == null || string.IsNullOrWhiteSpace(e.Uid) || e.Uid.Length > 64 || e.ExpiresUtc <= now) continue;
                if (list.Any(x => string.Equals(x.Uid, e.Uid, StringComparison.Ordinal)) || list.Count >= 5000) continue;
                list.Add(e);
            }
            target.TempPauses = list;
        });
        Try(nameof(VierAIConfig.BlockedUIDs), el =>
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                var s = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s) && !list.Contains(s, StringComparer.Ordinal)) list.Add(s);
            }
            target.BlockedUIDs = list;
        });
        Try(nameof(VierAIConfig.UIDsToPrioritize), el =>
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            target.UIDsToPrioritize = list;
        });
    }

    private static void CopyField(VierAIConfig from, VierAIConfig to, string name)
    {
        switch (name)
        {
            case nameof(VierAIConfig.NamePlatePairedSoundIndicator): to.NamePlatePairedSoundIndicator = from.NamePlatePairedSoundIndicator; break;
            case nameof(VierAIConfig.ShowNoiseMakersWindow): to.ShowNoiseMakersWindow = from.ShowNoiseMakersWindow; break;
            case nameof(VierAIConfig.UIDsToPrioritize): to.UIDsToPrioritize = new List<string>(from.UIDsToPrioritize); break;
        }
    }

    private static object GetField(VierAIConfig c, string name) => name switch
    {
        nameof(VierAIConfig.NamePlatePairedSoundIndicator) => c.NamePlatePairedSoundIndicator,
        nameof(VierAIConfig.ShowNoiseMakersWindow) => c.ShowNoiseMakersWindow,
        _ => string.Empty,
    };
}

/// <summary>
/// Saves vierai-config.json (debounced, atomic) whenever one of the shared config services saves, or a pass-through
/// setter marks it dirty, and logs what the startup load/migration did.
/// </summary>
public sealed class VierAIConfigService : IHostedService, IDisposable
{
    private readonly ILogger<VierAIConfigService> _logger;
    private readonly MareConfigService _mareConfig;
    private readonly PlayerPerformanceConfigService _performanceConfig;
    private readonly CancellationTokenSource _cts = new();

    public VierAIConfigService(ILogger<VierAIConfigService> logger, MareConfigService mareConfig, PlayerPerformanceConfigService performanceConfig)
    {
        _logger = logger;
        _mareConfig = mareConfig;
        _performanceConfig = performanceConfig;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var line in VierAIConfigStore.StartupLog)
        {
            if (line.Warning) _logger.LogWarning("{msg}", line.Text);
            else _logger.LogInformation("{msg}", line.Text);
        }
        _mareConfig.ConfigSave += OnSharedConfigSave;
        _performanceConfig.ConfigSave += OnSharedConfigSave;
        _ = Task.Run(() => SaveLoop(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _mareConfig.ConfigSave -= OnSharedConfigSave;
        _performanceConfig.ConfigSave -= OnSharedConfigSave;
        await _cts.CancelAsync().ConfigureAwait(false);
        Flush();
    }

    public void Dispose() => _cts.Dispose();

    private static void OnSharedConfigSave(object? sender, EventArgs e) => VierAIConfigStore.MarkDirty();

    private async Task SaveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (VierAIConfigStore.IsDirty) Flush();
        }
    }

    private void Flush()
    {
        if (!VierAIConfigStore.IsDirty) return;
        if (!VierAIConfigStore.TrySave(out var error))
            _logger.LogWarning("Could not save {file}: {error} (will retry)", VierAIConfigStore.FileName, error);
        else
            _logger.LogTrace("Saved {file}", VierAIConfigStore.FileName);
    }
}
