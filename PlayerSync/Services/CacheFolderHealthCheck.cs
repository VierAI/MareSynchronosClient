using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using MareSynchronos.MareConfiguration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace MareSynchronos.Services;

/// <summary>
/// Wine only (macOS/Linux), 1.15.5.910: warns when the cache (storage) folder looks like it is inside a cloud-synced or network
/// location (iCloud Documents/Desktop, Mobile Documents, CloudStorage, UNC or network drive). Such folders can have files evicted,
/// which shows up as missing-file or read failures. It is only a heuristic, so the wording is conditional.
/// One Warning log line at startup (and when the folder changes), plus a dismissible red line near the Storage Folder setting.
/// Windows behaviour is unchanged (nothing is checked or shown).
/// </summary>
public sealed partial class CacheFolderHealthCheck : IHostedService
{
    public const string WarningText = "If this is an iCloud (or other cloud-synced) folder, files may be evicted and cause missing-file or read failures. Move the cache outside Documents/Desktop.";

    [GeneratedRegex(@"^z:\\users\\[^\\]+\\(documents|desktop)(\\|$)", RegexOptions.CultureInvariant)]
    private static partial Regex UserDocsOrDesktop();

    private static string? _cachedPath;
    private static bool _cachedResult;

    private readonly ILogger<CacheFolderHealthCheck> _logger;
    private readonly MareConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private string? _lastLoggedPath;

    public CacheFolderHealthCheck(ILogger<CacheFolderHealthCheck> logger, MareConfigService configService, DalamudUtilService dalamudUtil)
    {
        _logger = logger;
        _configService = configService;
        _dalamudUtil = dalamudUtil;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_dalamudUtil.IsWine) return Task.CompletedTask;
        _configService.ConfigSave += OnConfigSave;
        CheckAndLog();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _configService.ConfigSave -= OnConfigSave;
        return Task.CompletedTask;
    }

    private void OnConfigSave(object? sender, EventArgs e) => CheckAndLog();

    private void CheckAndLog()
    {
        try
        {
            var path = _configService.Current.CacheFolder;
            if (string.Equals(path, _lastLoggedPath, StringComparison.OrdinalIgnoreCase)) return;
            _lastLoggedPath = path;
            if (IsSuspicious(path))
            {
                _logger.LogWarning("Cache folder {path} looks like a cloud-synced or network location. {text}", path, WarningText);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache folder health check failed");
        }
    }

    /// <summary>Path heuristic. Cached per path so it is cheap to call every frame.</summary>
    public static bool IsSuspicious(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (string.Equals(path, _cachedPath, StringComparison.Ordinal)) return _cachedResult;

        var result = Evaluate(path);
        _cachedPath = path;
        _cachedResult = result;
        return result;
    }

    private static bool Evaluate(string path)
    {
        var p = path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        if (UserDocsOrDesktop().IsMatch(p)) return true;
        if (p.Contains("\\library\\mobile documents", StringComparison.Ordinal)
            || p.Contains("\\library\\cloudstorage", StringComparison.Ordinal)
            || p.Contains("\\icloud", StringComparison.Ordinal)) return true;
        if (p.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network) return true;
        }
        catch
        {
            // unknown drive type: not flagged
        }
        return false;
    }

    /// <summary>Red warning with a Dismiss button under the Storage Folder setting. Wine only, until dismissed.</summary>
    public static void DrawWarning(bool isWine, string? cacheFolder)
    {
        if (!isWine || VierAIConfigStore.Current.CacheFolderWarningDismissed || !IsSuspicious(cacheFolder)) return;

        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
        ImGui.TextWrapped(WarningText);
        ImGui.PopStyleColor();
        if (ImGui.SmallButton("Dismiss##cacheFolderWarning"))
        {
            VierAIConfigStore.Current.CacheFolderWarningDismissed = true;
            VierAIConfigStore.MarkDirty();
        }
    }
}