using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Models;
using SingBoxClient.Core.Platform;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Handles application auto-update checks and in-place binary replacement.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Check for an available update on application startup.
    /// </summary>
    Task<UpdateInfo?> CheckOnStartupAsync();

    /// <summary>
    /// Download and apply the update. Returns true if the update was applied
    /// and the application should restart.
    /// </summary>
    Task<bool> ApplyUpdateAsync(UpdateInfo update);

    /// <summary>
    /// Whether a pending update has been detected.
    /// </summary>
    bool IsUpdateAvailable { get; }

    /// <summary>
    /// Details of the pending update, or null if none.
    /// </summary>
    UpdateInfo? PendingUpdate { get; }

    /// <summary>
    /// Alias for <see cref="CheckOnStartupAsync"/>. Used by Desktop ViewModels.
    /// </summary>
    Task<UpdateInfo?> CheckForUpdateAsync() => CheckOnStartupAsync();
}

/// <summary>
/// Default implementation with periodic background checks and in-place binary replacement.
/// </summary>
public class UpdateService : IUpdateService, IDisposable
{
    private readonly ILogger _logger = Log.ForContext<UpdateService>();
    private readonly IApiClient _apiClient;
    private readonly IPlatformService _platform;

    private Timer? _backgroundTimer;
    private bool _disposed;

    public bool IsUpdateAvailable => PendingUpdate is { Available: true };
    public UpdateInfo? PendingUpdate { get; private set; }

    public UpdateService(IApiClient apiClient, IPlatformService platform)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));

        // Start background periodic check
        _backgroundTimer = new Timer(
            _ => _ = BackgroundCheckAsync(),
            null,
            AppDefaults.UpdateCheckIntervalMs,
            AppDefaults.UpdateCheckIntervalMs);
    }

    /// <inheritdoc />
    public Task<UpdateInfo?> CheckForUpdateAsync() => CheckOnStartupAsync();

    // ── Check ────────────────────────────────────────────────────────────────

    public async Task<UpdateInfo?> CheckOnStartupAsync()
    {
        var arch = RuntimeInformation.OSArchitecture.ToString();
        var info = await _apiClient.CheckUpdateAsync(AppDefaults.Version, arch);

        if (info is { Available: true })
        {
            PendingUpdate = info;
            _logger.Information("Update available: {Version} (current: {Current})",
                info.Version, AppDefaults.Version);
        }
        else
        {
            _logger.Debug("No update available (current: {Current})", AppDefaults.Version);
        }

        return info;
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    public async Task<bool> ApplyUpdateAsync(UpdateInfo update)
    {
        if (update is null)
            throw new ArgumentNullException(nameof(update));

        if (string.IsNullOrEmpty(update.DownloadUrl))
        {
            _logger.Error("Update download URL is empty");
            return false;
        }

        try
        {
            var updateDir = Path.Combine(AppDefaults.ConfigDir, "update");
            Directory.CreateDirectory(updateDir);

            // Download the update package
            _logger.Information("Downloading update from {Url}", update.DownloadUrl);
            using var stream = await _apiClient.DownloadUpdateAsync(update.DownloadUrl);
            if (stream is null)
            {
                _logger.Error("Failed to download update — stream was null");
                return false;
            }

            var tempFile = Path.Combine(updateDir, "update_package.tmp");
            await using (var fs = File.Create(tempFile))
            {
                await stream.CopyToAsync(fs);
            }

            _logger.Information("Update downloaded to {Path}", tempFile);

            // Rename current executable to .bak
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe))
            {
                _logger.Error("Cannot determine current process path");
                return false;
            }

            var backupPath = currentExe + ".bak";

            // Remove stale backup if it exists
            if (File.Exists(backupPath))
                File.Delete(backupPath);

            File.Move(currentExe, backupPath);
            _logger.Debug("Current exe backed up to {Backup}", backupPath);

            // Move new binary into place
            File.Move(tempFile, currentExe);
            _logger.Information("New binary installed at {Path}", currentExe);

            // Restart the process with cleanup flag
            var psi = new ProcessStartInfo
            {
                FileName = currentExe,
                Arguments = "--cleanup-update",
                UseShellExecute = false
            };

            Process.Start(psi);
            _logger.Information("Restarting application with --cleanup-update flag");

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to apply update");
            return false;
        }
    }

    // ── Background check ─────────────────────────────────────────────────────

    private async Task BackgroundCheckAsync()
    {
        try
        {
            var arch = RuntimeInformation.OSArchitecture.ToString();
            var info = await _apiClient.CheckUpdateAsync(AppDefaults.Version, arch);

            if (info is { Available: true })
            {
                PendingUpdate = info;
                _logger.Information("Background check found update: {Version}", info.Version);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Background update check failed");
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _backgroundTimer?.Dispose();
        _backgroundTimer = null;
        GC.SuppressFinalize(this);
    }
}
