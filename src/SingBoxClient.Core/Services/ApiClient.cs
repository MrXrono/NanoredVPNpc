using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Helpers;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// HTTP client for all backend API communication.
/// </summary>
public interface IApiClient
{
    /// <summary>
    /// Check whether a newer application version is available.
    /// </summary>
    Task<UpdateInfo?> CheckUpdateAsync(string currentVersion, string arch);

    /// <summary>
    /// Download an update package as a stream.
    /// </summary>
    Task<Stream?> DownloadUpdateAsync(string url);

    /// <summary>
    /// Send a crash report to the backend.
    /// </summary>
    Task SendCrashLogAsync(string stackTrace, string appVersion, string osInfo);

    /// <summary>
    /// Send a batch of analytics events.
    /// </summary>
    Task SendAnalyticsAsync(List<AnalyticsEvent> events);

    /// <summary>
    /// Check if the backend has requested debug log collection from this client.
    /// </summary>
    Task<bool> IsDebugRequestedAsync();

    /// <summary>
    /// Upload debug logs to the backend.
    /// </summary>
    Task SendDebugLogsAsync(string logs);

    /// <summary>
    /// Fetch the current subscription status.
    /// </summary>
    Task<SubscriptionData?> GetSubscriptionStatusAsync();

    /// <summary>
    /// Fetch remote routing rules from the backend.
    /// </summary>
    Task<List<RoutingRule>?> GetRemoteConfigAsync();

    /// <summary>
    /// Fetch announcements published after the given timestamp.
    /// </summary>
    Task<List<Announcement>> GetAnnouncementsAsync(DateTime? since);
}

/// <summary>
/// Default implementation using <see cref="HttpClientFactory.CreateApiClient"/>.
/// All methods are resilient: they return null / empty on failure and log errors.
/// </summary>
public class ApiClient : IApiClient, IDisposable
{
    private readonly ILogger _logger = Log.ForContext<ApiClient>();
    private readonly HttpClient _http;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ApiClient()
    {
        _http = HttpClientFactory.CreateApiClient(ApiEndpoints.BaseUrl);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    public async Task<UpdateInfo?> CheckUpdateAsync(string currentVersion, string arch)
    {
        try
        {
            var url = $"{ApiEndpoints.UpdateCheck}?v={Uri.EscapeDataString(currentVersion)}&arch={Uri.EscapeDataString(arch)}";
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var info = await response.Content.ReadFromJsonAsync<UpdateInfo>(JsonOptions);
            return info;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to check for updates");
            return null;
        }
    }

    public async Task<Stream?> DownloadUpdateAsync(string url)
    {
        try
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to download update from {Url}", url);
            return null;
        }
    }

    // ── Analytics ────────────────────────────────────────────────────────────

    public async Task SendCrashLogAsync(string stackTrace, string appVersion, string osInfo)
    {
        try
        {
            var payload = new
            {
                stack_trace = stackTrace,
                app_version = appVersion,
                os_info = osInfo
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _http.PostAsync(ApiEndpoints.CrashLog, content);
            response.EnsureSuccessStatusCode();

            _logger.Debug("Crash log sent successfully");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send crash log");
        }
    }

    public async Task SendAnalyticsAsync(List<AnalyticsEvent> events)
    {
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(events, JsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _http.PostAsync(ApiEndpoints.AnalyticsEvent, content);
            response.EnsureSuccessStatusCode();

            _logger.Debug("Sent {Count} analytics events", events.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send {Count} analytics events", events.Count);
        }
    }

    // ── Debug ────────────────────────────────────────────────────────────────

    public async Task<bool> IsDebugRequestedAsync()
    {
        try
        {
            var response = await _http.GetAsync(ApiEndpoints.DebugRequest);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("debug_requested", out var val))
                return val.GetBoolean();

            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to check debug request status");
            return false;
        }
    }

    public async Task SendDebugLogsAsync(string logs)
    {
        try
        {
            var payload = new { logs };
            var content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _http.PostAsync(ApiEndpoints.DebugLogs, content);
            response.EnsureSuccessStatusCode();

            _logger.Debug("Debug logs sent ({Len} chars)", logs.Length);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send debug logs");
        }
    }

    // ── Subscription ─────────────────────────────────────────────────────────

    public async Task<SubscriptionData?> GetSubscriptionStatusAsync()
    {
        try
        {
            var response = await _http.GetAsync(ApiEndpoints.SubscriptionStatus);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<SubscriptionData>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch subscription status");
            return null;
        }
    }

    // ── Remote config ────────────────────────────────────────────────────────

    public async Task<List<RoutingRule>?> GetRemoteConfigAsync()
    {
        try
        {
            var response = await _http.GetAsync(ApiEndpoints.RemoteConfig);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<List<RoutingRule>>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch remote config");
            return null;
        }
    }

    // ── Announcements ────────────────────────────────────────────────────────

    public async Task<List<Announcement>> GetAnnouncementsAsync(DateTime? since)
    {
        try
        {
            var url = ApiEndpoints.Announcements;
            if (since.HasValue)
                url += $"?since={since.Value:o}";

            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<List<Announcement>>(JsonOptions)
                   ?? new List<Announcement>();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch announcements");
            return new List<Announcement>();
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
