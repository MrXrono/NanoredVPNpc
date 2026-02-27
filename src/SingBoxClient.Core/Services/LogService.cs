using Serilog;
using SingBoxClient.Core.Constants;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Manages sing-box log output: appending, rotation, and retrieval.
/// </summary>
public interface ILogService
{
    /// <summary>
    /// Append a single log line from the sing-box process output.
    /// </summary>
    void AppendLine(string line);

    /// <summary>
    /// Rotate the log file if it exceeds the maximum size threshold.
    /// </summary>
    void RotateIfNeeded();

    /// <summary>
    /// Read the most recent log lines from the current log file.
    /// </summary>
    string GetRecentLogs(int maxLines = 1000);

    /// <summary>
    /// Fired for each new log line appended (used for real-time UI binding).
    /// </summary>
    event Action<string>? OnNewLogLine;
}

/// <summary>
/// Default file-backed implementation with automatic rotation.
/// </summary>
public class LogService : ILogService
{
    private readonly ILogger _logger = Log.ForContext<LogService>();
    private readonly string _logFilePath;
    private readonly string _logsDir;
    private readonly object _writeLock = new();

    private StreamWriter? _writer;

    public event Action<string>? OnNewLogLine;

    public LogService()
    {
        _logsDir = AppDefaults.LogsDir;
        _logFilePath = Path.Combine(_logsDir, "singbox.log");

        EnsureLogsDirectory();
    }

    // ── Append ───────────────────────────────────────────────────────────────

    public void AppendLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        lock (_writeLock)
        {
            try
            {
                EnsureWriter();
                _writer!.WriteLine(line);
                _writer.Flush();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to write log line");
            }
        }

        OnNewLogLine?.Invoke(line);
    }

    // ── Rotation ─────────────────────────────────────────────────────────────

    public void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_logFilePath))
                return;

            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Length < AppDefaults.MaxLogFileSizeBytes)
                return;

            lock (_writeLock)
            {
                // Close current writer before rotating
                CloseWriter();

                // Shift existing rotated files: .2 → .3 (delete), .1 → .2, current → .1
                for (int i = AppDefaults.MaxLogFiles; i >= 1; i--)
                {
                    var src = i == 1 ? _logFilePath : GetRotatedPath(i - 1);
                    var dst = GetRotatedPath(i);

                    if (i == AppDefaults.MaxLogFiles && File.Exists(dst))
                        File.Delete(dst);

                    if (File.Exists(src))
                        File.Move(src, dst, overwrite: true);
                }

                _logger.Information("Log file rotated (was {Size} bytes)", fileInfo.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to rotate log file");
        }
    }

    // ── Recent logs ──────────────────────────────────────────────────────────

    public string GetRecentLogs(int maxLines = 1000)
    {
        try
        {
            if (!File.Exists(_logFilePath))
                return string.Empty;

            lock (_writeLock)
            {
                // Flush before reading
                _writer?.Flush();
            }

            var allLines = File.ReadAllLines(_logFilePath);
            var startIndex = Math.Max(0, allLines.Length - maxLines);
            var recentLines = allLines.Skip(startIndex).ToArray();

            return string.Join(Environment.NewLine, recentLines);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read recent logs");
            return string.Empty;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void EnsureLogsDirectory()
    {
        try
        {
            if (!Directory.Exists(_logsDir))
                Directory.CreateDirectory(_logsDir);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create logs directory: {Dir}", _logsDir);
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
            return;

        EnsureLogsDirectory();
        _writer = new StreamWriter(_logFilePath, append: true)
        {
            AutoFlush = false
        };
    }

    private void CloseWriter()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
    }

    private string GetRotatedPath(int index)
    {
        return Path.Combine(_logsDir, $"singbox.{index}.log");
    }
}
