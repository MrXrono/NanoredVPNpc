using System.Text.Json.Serialization;

namespace SingBoxClient.Core.Models;

/// <summary>
/// Application settings persisted to JSON on disk.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Enable system-level proxy (SOCKS5/HTTP).
    /// </summary>
    [JsonPropertyName("proxy_enabled")]
    public bool ProxyEnabled { get; set; } = true;

    /// <summary>
    /// Enable TUN mode (virtual network adapter).
    /// </summary>
    [JsonPropertyName("tun_enabled")]
    public bool TunEnabled { get; set; } = false;

    /// <summary>
    /// Local SOCKS5/HTTP proxy listen port.
    /// </summary>
    [JsonPropertyName("proxy_port")]
    public int ProxyPort { get; set; } = 2080;

    /// <summary>
    /// Currently selected country code (ISO 3166-1 alpha-2, e.g. "DE").
    /// Empty string means auto-select best server.
    /// </summary>
    [JsonPropertyName("selected_country")]
    public string SelectedCountry { get; set; } = string.Empty;

    /// <summary>
    /// UI language: "ru" or "en".
    /// </summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "ru";

    /// <summary>
    /// UI theme: "dark" or "light".
    /// </summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark";

    /// <summary>
    /// Minimize to system tray instead of closing.
    /// </summary>
    [JsonPropertyName("minimize_to_tray")]
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// Launch application on system startup.
    /// </summary>
    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; } = false;

    /// <summary>
    /// Automatically connect to the last used server on launch.
    /// </summary>
    [JsonPropertyName("auto_connect")]
    public bool AutoConnect { get; set; } = false;

    /// <summary>
    /// Allow fetching routing rules and announcements from the remote server.
    /// </summary>
    [JsonPropertyName("remote_config_enabled")]
    public bool RemoteConfigEnabled { get; set; } = true;

    /// <summary>
    /// Enable verbose debug logging.
    /// </summary>
    [JsonPropertyName("debug_mode")]
    public bool DebugMode { get; set; } = true;

    /// <summary>
    /// Applications excluded from TUN tunnel (bypass list).
    /// </summary>
    [JsonPropertyName("tun_bypass_apps")]
    public List<string> TunBypassApps { get; set; } = new();

    /// <summary>
    /// Applications forced through TUN tunnel (include-only list).
    /// </summary>
    [JsonPropertyName("tun_proxy_apps")]
    public List<string> TunProxyApps { get; set; } = new();

    /// <summary>
    /// Applications whose traffic is blocked entirely.
    /// </summary>
    [JsonPropertyName("tun_block_apps")]
    public List<string> TunBlockApps { get; set; } = new();

    /// <summary>
    /// Subscription URL for fetching server configurations.
    /// </summary>
    [JsonPropertyName("subscription_url")]
    public string SubscriptionUrl { get; set; } = string.Empty;

    /// <summary>
    /// Saved window width (0 = use default).
    /// </summary>
    [JsonPropertyName("window_width")]
    public double WindowWidth { get; set; }

    /// <summary>
    /// Saved window height (0 = use default).
    /// </summary>
    [JsonPropertyName("window_height")]
    public double WindowHeight { get; set; }

    /// <summary>
    /// Saved window X position (NaN = center on screen).
    /// </summary>
    [JsonPropertyName("window_x")]
    public double WindowX { get; set; } = double.NaN;

    /// <summary>
    /// Saved window Y position (NaN = center on screen).
    /// </summary>
    [JsonPropertyName("window_y")]
    public double WindowY { get; set; } = double.NaN;

    /// <summary>
    /// Whether the window was maximized.
    /// </summary>
    [JsonPropertyName("window_maximized")]
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Settings schema version for migration support.
    /// </summary>
    [JsonPropertyName("settings_version")]
    public int SettingsVersion { get; set; } = 1;
}
