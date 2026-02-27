using System.Net.Http.Json;
using System.Text.Json;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Client for the sing-box Clash-compatible REST API (127.0.0.1:9090).
/// </summary>
public interface IClashApiClient
{
    /// <summary>
    /// Check whether the Clash API is reachable and healthy.
    /// </summary>
    Task<bool> HealthCheckAsync();

    /// <summary>
    /// Fetch current real-time traffic statistics.
    /// </summary>
    Task<TrafficStats> GetTrafficAsync();

    /// <summary>
    /// Close all active proxy connections.
    /// </summary>
    Task CloseAllConnectionsAsync();

    /// <summary>
    /// Get the full proxies tree as raw JSON.
    /// </summary>
    Task<string> GetProxiesAsync();
}

/// <summary>
/// Default HTTP-based implementation of <see cref="IClashApiClient"/>.
/// </summary>
public class ClashApiClient : IClashApiClient, IDisposable
{
    private readonly ILogger _logger = Log.ForContext<ClashApiClient>();
    private readonly HttpClient _http;
    private bool _disposed;

    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ClashApiClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(AppDefaults.ClashApiUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    // ── Health Check ─────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync()
    {
        try
        {
            var response = await ExecuteWithRetryAsync(() => _http.GetAsync("/"));
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Clash API health check failed");
            return false;
        }
    }

    // ── Traffic ──────────────────────────────────────────────────────────

    public async Task<TrafficStats> GetTrafficAsync()
    {
        try
        {
            var response = await ExecuteWithRetryAsync(() => _http.GetAsync("/traffic"));
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();

            // The /traffic endpoint returns streaming JSON lines: {"up":N,"down":N}
            // We parse the last complete line.
            var lastLine = GetLastJsonLine(json);
            if (string.IsNullOrWhiteSpace(lastLine))
                return new TrafficStats();

            using var doc = JsonDocument.Parse(lastLine);
            var root = doc.RootElement;

            return new TrafficStats
            {
                UploadSpeed = root.TryGetProperty("up", out var up) ? up.GetInt64() : 0,
                DownloadSpeed = root.TryGetProperty("down", out var down) ? down.GetInt64() : 0,
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch traffic stats from Clash API");
            return new TrafficStats();
        }
    }

    // ── Connections ──────────────────────────────────────────────────────

    public async Task CloseAllConnectionsAsync()
    {
        try
        {
            var response = await ExecuteWithRetryAsync(() => _http.DeleteAsync("/connections"));
            response.EnsureSuccessStatusCode();
            _logger.Information("All Clash API connections closed");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to close Clash API connections");
        }
    }

    // ── Proxies ──────────────────────────────────────────────────────────

    public async Task<string> GetProxiesAsync()
    {
        try
        {
            var response = await ExecuteWithRetryAsync(() => _http.GetAsync("/proxies"));
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch proxies from Clash API");
            return "{}";
        }
    }

    // ── Retry Logic ──────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> ExecuteWithRetryAsync(
        Func<Task<HttpResponseMessage>> action)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                lastException = ex;
                _logger.Debug(ex, "Clash API request failed (attempt {Attempt}/{Max}), retrying",
                    attempt, MaxRetries);
                await Task.Delay(RetryDelay);
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new InvalidOperationException("Retry logic completed without result");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string? GetLastJsonLine(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : null;
    }

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
