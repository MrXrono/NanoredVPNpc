using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Collects telemetry events and crash reports, buffering them for batch delivery.
/// </summary>
public interface IAnalyticsService
{
    /// <summary>
    /// Track a named event with optional properties. Events are buffered and sent in batches.
    /// </summary>
    void Track(string eventName, Dictionary<string, string>? properties = null);

    /// <summary>
    /// Capture and send a crash report. Also persists it to disk for deferred delivery.
    /// </summary>
    Task SendCrashLogAsync(Exception ex);

    /// <summary>
    /// Force-flush all buffered events to the backend immediately.
    /// </summary>
    Task FlushAsync();

    /// <summary>
    /// Track a structured analytics event. Used by Desktop ViewModels.
    /// </summary>
    Task TrackAsync(AnalyticsEvent evt);
}

/// <summary>
/// Default implementation with an in-memory buffer, periodic flush, and crash log persistence.
/// </summary>
public class AnalyticsService : IAnalyticsService, IDisposable
{
    private readonly ILogger _logger = Log.ForContext<AnalyticsService>();
    private readonly IApiClient _apiClient;

    private readonly ConcurrentQueue<AnalyticsEvent> _buffer = new();
    private Timer? _flushTimer;
    private bool _disposed;

    public AnalyticsService(IApiClient apiClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));

        // Periodic flush timer
        _flushTimer = new Timer(
            _ => _ = FlushAsync(),
            null,
            AppDefaults.AnalyticsFlushIntervalMs,
            AppDefaults.AnalyticsFlushIntervalMs);

        // On startup, send any unsent crash logs from previous sessions
        _ = SendUnsentCrashLogsAsync();
    }

    // ── Track ────────────────────────────────────────────────────────────────

    public void Track(string eventName, Dictionary<string, string>? properties = null)
    {
        var evt = new AnalyticsEvent
        {
            EventName = eventName,
            Properties = properties ?? new Dictionary<string, string>(),
            Timestamp = DateTime.UtcNow
        };

        _buffer.Enqueue(evt);

        // Auto-flush when buffer reaches threshold
        if (_buffer.Count >= AppDefaults.AnalyticsBatchSize)
        {
            _ = FlushAsync();
        }
    }

    // ── Track Async ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task TrackAsync(AnalyticsEvent evt)
    {
        if (evt is null)
            return Task.CompletedTask;

        _buffer.Enqueue(evt);

        // Auto-flush when buffer reaches threshold
        if (_buffer.Count >= AppDefaults.AnalyticsBatchSize)
        {
            return FlushAsync();
        }

        return Task.CompletedTask;
    }

    // ── Crash log ────────────────────────────────────────────────────────────

    public async Task SendCrashLogAsync(Exception ex)
    {
        if (ex is null)
            return;

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var crashInfo = BuildCrashInfo(ex);

        // Persist to disk first (in case send fails)
        try
        {
            Directory.CreateDirectory(AppDefaults.LogsDir);
            var crashFile = Path.Combine(AppDefaults.LogsDir, $"crash_{timestamp}.log");
            await File.WriteAllTextAsync(crashFile, crashInfo);
            _logger.Debug("Crash log saved to {Path}", crashFile);
        }
        catch (Exception writeEx)
        {
            _logger.Error(writeEx, "Failed to persist crash log to disk");
        }

        // Attempt immediate delivery
        try
        {
            await _apiClient.SendCrashLogAsync(
                crashInfo,
                AppDefaults.Version,
                $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}");
        }
        catch (Exception sendEx)
        {
            _logger.Error(sendEx, "Failed to send crash log to backend (will retry on next launch)");
        }
    }

    // ── Flush ────────────────────────────────────────────────────────────────

    public async Task FlushAsync()
    {
        if (_buffer.IsEmpty)
            return;

        var batch = new List<AnalyticsEvent>();

        while (_buffer.TryDequeue(out var evt))
            batch.Add(evt);

        if (batch.Count == 0)
            return;

        try
        {
            await _apiClient.SendAnalyticsAsync(batch);
            _logger.Debug("Flushed {Count} analytics events", batch.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to flush {Count} analytics events — re-enqueuing", batch.Count);

            // Re-enqueue failed events for next attempt
            foreach (var evt in batch)
                _buffer.Enqueue(evt);
        }
    }

    // ── Unsent crash logs ────────────────────────────────────────────────────

    /// <summary>
    /// On startup, scan for crash_*.log files that were not successfully sent,
    /// attempt to send them, and delete on success.
    /// </summary>
    private async Task SendUnsentCrashLogsAsync()
    {
        try
        {
            if (!Directory.Exists(AppDefaults.LogsDir))
                return;

            var crashFiles = Directory.GetFiles(AppDefaults.LogsDir, "crash_*.log");
            if (crashFiles.Length == 0)
                return;

            _logger.Information("Found {Count} unsent crash log(s)", crashFiles.Length);

            foreach (var file in crashFiles)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file);

                    await _apiClient.SendCrashLogAsync(
                        content,
                        AppDefaults.Version,
                        $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}");

                    // Delete after successful send
                    File.Delete(file);
                    _logger.Debug("Sent and deleted unsent crash log: {File}", file);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to send unsent crash log: {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process unsent crash logs");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildCrashInfo(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Timestamp: {DateTime.UtcNow:O}");
        sb.AppendLine($"App Version: {AppDefaults.Version}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Arch: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine();
        sb.AppendLine("--- Exception ---");
        sb.AppendLine($"Type: {ex.GetType().FullName}");
        sb.AppendLine($"Message: {ex.Message}");
        sb.AppendLine($"Stack Trace:\n{ex.StackTrace}");

        if (ex.InnerException is not null)
        {
            sb.AppendLine();
            sb.AppendLine("--- Inner Exception ---");
            sb.AppendLine($"Type: {ex.InnerException.GetType().FullName}");
            sb.AppendLine($"Message: {ex.InnerException.Message}");
            sb.AppendLine($"Stack Trace:\n{ex.InnerException.StackTrace}");
        }

        return sb.ToString();
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _flushTimer?.Dispose();
        _flushTimer = null;

        // Best-effort final flush (fire-and-forget)
        _ = FlushAsync();

        GC.SuppressFinalize(this);
    }
}
