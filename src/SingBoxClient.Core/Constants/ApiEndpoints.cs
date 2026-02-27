namespace SingBoxClient.Core.Constants;

/// <summary>
/// Backend API endpoint paths. Combine with <see cref="BaseUrl"/> to form full URIs.
/// </summary>
public static class ApiEndpoints
{
    // ── Base ─────────────────────────────────────────────────────────────
    public const string BaseUrl = "https://api.example.com";

    // ── Analytics ────────────────────────────────────────────────────────
    public const string CrashLog = "/api/v1/analytics/crash";
    public const string AnalyticsEvent = "/api/v1/analytics/event";
    public const string DebugRequest = "/api/v1/analytics/debug-request";
    public const string DebugLogs = "/api/v1/analytics/debug-logs";

    // ── Update ──────────────────────────────────────────────────────────
    public const string UpdateCheck = "/api/v1/update/check";
    public const string UpdateDownload = "/api/v1/update/download";

    // ── Subscription ────────────────────────────────────────────────────
    public const string SubscriptionStatus = "/api/v1/subscription/status";

    // ── Config ──────────────────────────────────────────────────────────
    public const string RemoteConfig = "/api/v1/config/remote";

    // ── Announcements ───────────────────────────────────────────────────
    public const string Announcements = "/api/v1/announcements";
}
