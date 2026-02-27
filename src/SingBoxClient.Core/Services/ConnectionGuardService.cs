using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Monitors the VPN connection health and performs automatic reconnection
/// when the active server becomes unreachable.
/// </summary>
public interface IConnectionGuardService
{
    /// <summary>
    /// Begin monitoring the connection to the given server within the specified country group.
    /// </summary>
    void StartMonitoring(CountryGroup country, ServerNode activeServer);

    /// <summary>
    /// Stop the monitoring loop. Does not stop the sing-box process.
    /// </summary>
    void StopMonitoring();

    /// <summary>
    /// Fired when the connection status changes during monitoring.
    /// </summary>
    event Action<ConnectionStatus>? OnStatusChanged;
}

/// <summary>
/// Default implementation that uses Clash API health checks and automatic server failover.
/// </summary>
public class ConnectionGuardService : IConnectionGuardService, IDisposable
{
    private readonly ILogger _logger = Log.ForContext<ConnectionGuardService>();
    private readonly IClashApiClient _clashApi;
    private readonly ISingBoxProcessManager _processManager;
    private readonly ISingBoxConfigBuilder _configBuilder;

    private CancellationTokenSource? _monitorCts;
    private CountryGroup? _currentCountry;
    private ServerNode? _activeServer;
    private bool _disposed;

    public event Action<ConnectionStatus>? OnStatusChanged;

    public ConnectionGuardService(
        IClashApiClient clashApi,
        ISingBoxProcessManager processManager,
        ISingBoxConfigBuilder configBuilder)
    {
        _clashApi = clashApi ?? throw new ArgumentNullException(nameof(clashApi));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _configBuilder = configBuilder ?? throw new ArgumentNullException(nameof(configBuilder));
    }

    // ── Public API ──────────────────────────────────────────────────────────

    public void StartMonitoring(CountryGroup country, ServerNode activeServer)
    {
        StopMonitoring();

        _currentCountry = country ?? throw new ArgumentNullException(nameof(country));
        _activeServer = activeServer ?? throw new ArgumentNullException(nameof(activeServer));

        _monitorCts = new CancellationTokenSource();
        _ = MonitorLoopAsync(_monitorCts.Token);

        _logger.Information(
            "Connection guard started for {Server} in {Country}",
            activeServer.Name, country.DisplayName);
    }

    public void StopMonitoring()
    {
        if (_monitorCts is null)
            return;

        _logger.Debug("Stopping connection guard monitoring");
        _monitorCts.Cancel();
        _monitorCts.Dispose();
        _monitorCts = null;
    }

    // ── Monitor loop ────────────────────────────────────────────────────────

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        int consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(AppDefaults.HealthCheckIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var healthy = await _clashApi.HealthCheckAsync(ct);

                if (healthy)
                {
                    if (consecutiveFailures > 0)
                    {
                        _logger.Information("Health check recovered after {Failures} failure(s)", consecutiveFailures);
                        consecutiveFailures = 0;
                        OnStatusChanged?.Invoke(ConnectionStatus.Connected);
                    }
                    continue;
                }

                consecutiveFailures++;
                _logger.Warning("Health check failed ({Count}/{Max})",
                    consecutiveFailures, AppDefaults.HealthCheckMaxFailures);

                if (consecutiveFailures >= AppDefaults.HealthCheckMaxFailures)
                {
                    OnStatusChanged?.Invoke(ConnectionStatus.Reconnecting);
                    var reconnected = await TryReconnectAsync(ct);

                    if (reconnected)
                    {
                        consecutiveFailures = 0;
                        OnStatusChanged?.Invoke(ConnectionStatus.Connected);
                    }
                    else
                    {
                        _logger.Error("All reconnection attempts failed — disconnecting");
                        await _processManager.StopAsync();
                        OnStatusChanged?.Invoke(ConnectionStatus.Disconnected);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error in connection guard loop");
                consecutiveFailures++;
            }
        }
    }

    // ── Reconnect logic ─────────────────────────────────────────────────────

    private async Task<bool> TryReconnectAsync(CancellationToken ct)
    {
        // 1. Try restarting with the same active server first
        if (_activeServer is not null)
        {
            _logger.Information("Attempting reconnect with current server: {Server}", _activeServer.Name);

            if (await RestartWithServerAsync(_activeServer, ct))
                return true;
        }

        // 2. Fall back to other servers in the same country, ordered by latency
        if (_currentCountry is null)
            return false;

        var fallbackServers = _currentCountry.Servers
            .Where(s => s != _activeServer && s.IsReachable)
            .OrderBy(s => s.Latency)
            .ToList();

        foreach (var server in fallbackServers)
        {
            ct.ThrowIfCancellationRequested();

            _logger.Information("Trying fallback server: {Server} (latency {Latency}ms)",
                server.Name, server.Latency);

            if (await RestartWithServerAsync(server, ct))
            {
                _activeServer = server;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Regenerate sing-box configuration for the given server, restart the process,
    /// and verify health after a short delay.
    /// </summary>
    private async Task<bool> RestartWithServerAsync(ServerNode server, CancellationToken ct)
    {
        try
        {
            var configPath = _configBuilder.BuildAndSave(server);
            await _processManager.RestartAsync(configPath);

            // Wait for sing-box to initialize
            await Task.Delay(AppDefaults.ReconnectDelayMs, ct);

            return await _clashApi.HealthCheckAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to restart with server {Server}", server.Name);
            return false;
        }
    }

    // ── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopMonitoring();
        GC.SuppressFinalize(this);
    }
}

// ── Forward-declared interfaces ─────────────────────────────────────────────
// These will be implemented in separate files as the project grows.

/// <summary>
/// Client for the sing-box Clash-compatible API used for health checks and traffic stats.
/// </summary>
public interface IClashApiClient
{
    /// <summary>
    /// Check if the sing-box core is healthy and responsive.
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}

/// <summary>
/// Builds sing-box JSON configuration files for a given server.
/// </summary>
public interface ISingBoxConfigBuilder
{
    /// <summary>
    /// Build a complete sing-box configuration for the given server and write it to disk.
    /// </summary>
    /// <returns>Absolute path to the generated config.json file.</returns>
    string BuildAndSave(ServerNode server);
}
