using System.Diagnostics;
using System.Net.Sockets;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Measures latency to proxy servers via TCP connect probes.
/// </summary>
public interface IPingService
{
    /// <summary>
    /// Ping all servers concurrently and return latency results.
    /// </summary>
    /// <param name="servers">Servers to ping.</param>
    /// <returns>One <see cref="PingResult"/> per server.</returns>
    Task<List<PingResult>> PingAllAsync(List<ServerNode> servers);

    /// <summary>
    /// Get the server with the lowest latency in a country group.
    /// Returns null if no servers are reachable.
    /// </summary>
    ServerNode? GetBestInCountry(CountryGroup country);
}

/// <summary>
/// Default implementation using TCP connect with concurrency throttling
/// and median-of-N latency measurement.
/// </summary>
public class PingService : IPingService
{
    private readonly ILogger _logger = Log.ForContext<PingService>();

    // ── Ping All ─────────────────────────────────────────────────────────

    public async Task<List<PingResult>> PingAllAsync(List<ServerNode> servers)
    {
        if (servers is null || servers.Count == 0)
            return new List<PingResult>();

        _logger.Information("Starting ping for {Count} servers (max {Concurrent} concurrent)",
            servers.Count, AppDefaults.MaxConcurrentPings);

        var semaphore = new SemaphoreSlim(AppDefaults.MaxConcurrentPings);
        var tasks = new List<Task<PingResult>>(servers.Count);

        foreach (var server in servers)
        {
            tasks.Add(PingWithThrottleAsync(server, semaphore));
        }

        var results = await Task.WhenAll(tasks);
        var resultList = results.ToList();

        // Update server objects with measured latency
        foreach (var result in resultList)
        {
            result.Server.Latency = result.LatencyMs;
            result.Server.IsReachable = result.IsReachable;
        }

        var reachable = resultList.Count(r => r.IsReachable);
        _logger.Information("Ping complete: {Reachable}/{Total} servers reachable",
            reachable, resultList.Count);

        return resultList;
    }

    // ── Best In Country ──────────────────────────────────────────────────

    public ServerNode? GetBestInCountry(CountryGroup country)
    {
        if (country is null || country.Servers.Count == 0)
            return null;

        var reachable = country.Servers
            .Where(s => s.IsReachable && s.Latency >= 0)
            .OrderBy(s => s.Latency)
            .ToList();

        if (reachable.Count == 0)
            return null;

        var best = reachable.First();
        country.BestServer = best;
        country.AverageLatency = (int)reachable.Average(s => s.Latency);

        return best;
    }

    // ── Private: Single Server Ping ──────────────────────────────────────

    private async Task<PingResult> PingWithThrottleAsync(ServerNode server, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            return await PingSingleAsync(server);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<PingResult> PingSingleAsync(ServerNode server)
    {
        var result = new PingResult
        {
            Server = server,
            LatencyMs = -1,
            IsReachable = false,
        };

        if (string.IsNullOrWhiteSpace(server.Address) || server.Port <= 0)
        {
            _logger.Debug("Skipping ping for {Name}: invalid address/port", server.Name);
            return result;
        }

        try
        {
            var measurements = new List<long>(AppDefaults.PingRetries);

            for (var attempt = 0; attempt < AppDefaults.PingRetries; attempt++)
            {
                var latency = await TcpConnectAsync(server.Address, server.Port, AppDefaults.PingTimeoutMs);
                if (latency >= 0)
                    measurements.Add(latency);
            }

            if (measurements.Count > 0)
            {
                // Take median of successful measurements
                result.LatencyMs = (int)GetMedian(measurements);
                result.IsReachable = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Ping failed for {Name} ({Address}:{Port})",
                server.Name, server.Address, server.Port);
        }

        return result;
    }

    // ── Private: TCP Connect Probe ───────────────────────────────────────

    private static async Task<long> TcpConnectAsync(string host, int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);

            await tcp.ConnectAsync(host, port, cts.Token);
            sw.Stop();

            return sw.ElapsedMilliseconds;
        }
        catch (OperationCanceledException)
        {
            // Timeout
            return -1;
        }
        catch (SocketException)
        {
            // Connection refused or unreachable
            return -1;
        }
    }

    // ── Private: Statistics ──────────────────────────────────────────────

    private static long GetMedian(List<long> values)
    {
        if (values.Count == 0)
            return -1;

        values.Sort();
        var mid = values.Count / 2;

        return values.Count % 2 == 0
            ? (values[mid - 1] + values[mid]) / 2
            : values[mid];
    }
}
