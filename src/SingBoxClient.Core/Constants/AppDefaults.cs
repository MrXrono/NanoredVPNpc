namespace SingBoxClient.Core.Constants;

/// <summary>
/// Application-wide default constants and configuration values.
/// </summary>
public static class AppDefaults
{
    // ── Version ──────────────────────────────────────────────────────────
    public const string Version = "1.0.0";

    // ── Network Ports ────────────────────────────────────────────────────
    public const int ProxyPort = 2080;
    public const int ClashApiPort = 9090;
    public const string ClashApiUrl = "http://127.0.0.1:9090";

    // ── Ping / Latency ──────────────────────────────────────────────────
    public const int PingTimeoutMs = 5000;
    public const int PingRetries = 3;
    public const int MaxConcurrentPings = 10;

    // ── Health Check ────────────────────────────────────────────────────
    public const int HealthCheckIntervalMs = 5000;
    public const int HealthCheckMaxFailures = 3;
    public const int ReconnectDelayMs = 3000;

    // ── Subscription ────────────────────────────────────────────────────
    public const int SubscriptionRetries = 3;

    // ── Analytics ────────────────────────────────────────────────────────
    public const int AnalyticsBatchSize = 20;
    public const int AnalyticsFlushIntervalMs = 300_000;   // 5 min

    // ── Update ──────────────────────────────────────────────────────────
    public const int UpdateCheckIntervalMs = 1_800_000;    // 30 min

    // ── Debug ───────────────────────────────────────────────────────────
    public const int DebugPollIntervalMs = 60_000;

    // ── Logging ─────────────────────────────────────────────────────────
    public const long MaxLogFileSizeBytes = 10_485_760;    // 10 MB
    public const int MaxLogFiles = 3;

    // ── TUN Mode ────────────────────────────────────────────────────────
    public const string TunAddress = "172.19.0.1/30";

    // ── File Names / Paths ──────────────────────────────────────────────
    public const string SingBoxExe = "sing-box.exe";
    public const string ConfigFileName = "config.json";
    public const string SettingsFileName = "settings.json";
    public const string ServersFileName = "servers.json";
    public const string RoutingFileName = "routing.json";
    public const string ConfigDir = "Configuration";
    public const string LogsDir = "Logs";

    /// <summary>Kept for migration — old data directory path.</summary>
    [Obsolete("Use ConfigDir / LogsDir instead")]
    public const string DataDir = "data";

    // ── HTTP ─────────────────────────────────────────────────────────────
    public const string UserAgent = "NanoredVPN/1.0.0";
}
