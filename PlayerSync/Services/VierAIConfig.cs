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

    // Main window: show a green "(N)" next to each syncshell = members you reach through no other syncshell or direct pair.
    public bool ShowSyncshellUniqueCount { get; set; } = true;

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
    // Priority tiers (schema 3): tier 0 = UIDsToPrioritize, tier 1 = in-game friends, tier 2 = direct syncs, tier 3 = everyone else. All always on;
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