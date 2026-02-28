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
    Task<bool> HealthCheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Start streaming traffic stats from the /traffic endpoint.
    /// Invokes <paramref name="onStats"/> on each update (~1/sec from sing-box).
    /// Runs until <paramref name="ct"/> is cancelled or the stream ends.
    /// Automatically reconnects on transient failures.
    /// </summary>
    Task StreamTrafficAsync(Action<TrafficStats> onStats, CancellationToken ct);

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

    public ClashApiClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(AppDefaults.ClashApiUrl),
            // Infinite timeout: streaming /traffic keeps the connection open indefinitely.
            // Per-request timeouts are enforced via CancellationTokenSource where needed.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    // ── Health Check ─────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await _http.GetAsync("/", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Clash API health check failed");
            return false;
        }
    }

    // ── Traffic (streaming) ──────────────────────────────────────────────

    public async Task StreamTrafficAsync(Action<TrafficStats> onStats, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // ResponseHeadersRead: returns as soon as headers arrive,
                // body is read lazily via the stream — essential for streaming endpoints.
                using var response = await _http.GetAsync(
                    "/traffic", HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                // Read JSON lines as they arrive from sing-box (~1 line/sec)
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                        break; // stream closed by sing-box (process stopped)

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        var stats = new TrafficStats
                        {
                            UploadSpeed = root.TryGetProperty("up", out var up) ? up.GetInt64() : 0,
                            DownloadSpeed = root.TryGetProperty("down", out var down) ? down.GetInt64() : 0,
                        };

                        onStats(stats);
                    }
                    catch (JsonException ex)
                    {
                        _logger.Debug(ex, "Failed to parse traffic JSON line: {Line}", line);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Traffic stream interrupted, reconnecting in 2s");

                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // ── Connections ──────────────────────────────────────────────────────

    public async Task CloseAllConnectionsAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _http.DeleteAsync("/connections", cts.Token);
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _http.GetAsync("/proxies", cts.Token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch proxies from Clash API");
            return "{}";
        }
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
