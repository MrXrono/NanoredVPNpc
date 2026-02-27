using System.Text.Json;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Manages application settings with JSON persistence and schema migration.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Current application settings.
    /// </summary>
    AppSettings Settings { get; }

    /// <summary>
    /// Alias for <see cref="Settings"/> used by Desktop ViewModels.
    /// </summary>
    AppSettings Current => Settings;

    /// <summary>
    /// Load settings from disk. Creates defaults if the file does not exist.
    /// </summary>
    void Load();

    /// <summary>
    /// Persist current settings to disk.
    /// </summary>
    void Save();

    /// <summary>
    /// Fired after settings have been saved to disk.
    /// </summary>
    event Action? OnSettingsChanged;
}

/// <summary>
/// Default file-backed implementation that serializes settings to data/settings.json.
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly ILogger _logger = Log.ForContext<SettingsService>();
    private readonly string _filePath;

    private const int LatestSettingsVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppSettings Settings { get; private set; } = new();

    /// <inheritdoc />
    public AppSettings Current => Settings;

    public event Action? OnSettingsChanged;

    public SettingsService()
    {
        _filePath = Path.Combine(AppDefaults.DataDir, AppDefaults.SettingsFileName);
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    public void Load()
    {
        try
        {
            EnsureDataDirectory();

            if (!File.Exists(_filePath))
            {
                _logger.Information("Settings file not found at {Path}, creating defaults", _filePath);
                Settings = new AppSettings();
                Save();
                return;
            }

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);

            if (loaded is null)
            {
                _logger.Warning("Deserialized settings were null, using defaults");
                Settings = new AppSettings();
                Save();
                return;
            }

            Settings = loaded;

            // Run migrations if schema version is behind
            if (Settings.SettingsVersion < LatestSettingsVersion)
            {
                MigrateSettings();
                Save();
            }

            _logger.Information("Settings loaded from {Path} (version {V})",
                _filePath, Settings.SettingsVersion);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load settings from {Path}, using defaults", _filePath);
            Settings = new AppSettings();
        }
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    public void Save()
    {
        try
        {
            EnsureDataDirectory();

            var json = JsonSerializer.Serialize(Settings, SerializerOptions);
            File.WriteAllText(_filePath, json);

            _logger.Debug("Settings saved to {Path}", _filePath);
            OnSettingsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save settings to {Path}", _filePath);
        }
    }

    // ── Migration ────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply sequential migrations from the current version to the latest.
    /// Each migration step should increment <see cref="AppSettings.SettingsVersion"/>.
    /// </summary>
    private void MigrateSettings()
    {
        _logger.Information("Migrating settings from version {From} to {To}",
            Settings.SettingsVersion, LatestSettingsVersion);

        // Example migration chain:
        // if (Settings.SettingsVersion < 2) { MigrateV1ToV2(); }
        // if (Settings.SettingsVersion < 3) { MigrateV2ToV3(); }

        Settings.SettingsVersion = LatestSettingsVersion;
        _logger.Information("Settings migration complete (now version {V})", Settings.SettingsVersion);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void EnsureDataDirectory()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.Debug("Created data directory: {Dir}", directory);
        }
    }
}
