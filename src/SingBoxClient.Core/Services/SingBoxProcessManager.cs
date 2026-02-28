using System.Diagnostics;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Platform;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Manages the lifecycle of the sing-box core process (start, stop, restart).
/// </summary>
public interface ISingBoxProcessManager
{
    /// <summary>
    /// Whether the sing-box process is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Fired for every stdout/stderr line emitted by sing-box.
    /// </summary>
    event Action<string>? OnLogLine;

    /// <summary>
    /// Fired when the sing-box process exits. Argument is the exit code.
    /// </summary>
    event Action<int>? OnProcessExited;

    /// <summary>
    /// Start sing-box with the given configuration file.
    /// </summary>
    Task StartAsync(string configPath);

    /// <summary>
    /// Stop the running sing-box process gracefully, force-killing after timeout.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Stop and then start sing-box with the given configuration file.
    /// </summary>
    Task RestartAsync(string configPath);
}

/// <summary>
/// Default implementation that spawns sing-box as a child process.
/// Thread-safe: all operations are serialized via <see cref="_gate"/>.
/// </summary>
public class SingBoxProcessManager : ISingBoxProcessManager, IDisposable
{
    private readonly ILogger _logger = Log.ForContext<SingBoxProcessManager>();
    private readonly IPlatformService _platform;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private CancellationTokenSource? _outputCts;
    private bool _disposed;

    private const int GracefulStopTimeoutMs = 3000;
    private const int ForceKillTimeoutMs = 2000;

    public bool IsRunning
    {
        get
        {
            var proc = _process;
            try { return proc is not null && !proc.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public event Action<string>? OnLogLine;
    public event Action<int>? OnProcessExited;

    public SingBoxProcessManager(IPlatformService platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    // ── Start ────────────────────────────────────────────────────────────

    public async Task StartAsync(string configPath)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning)
            {
                _logger.Warning("sing-box is already running (PID {Pid}), ignoring StartAsync", _process!.Id);
                return;
            }

            var exePath = ResolveSingBoxPath();
            if (!File.Exists(exePath))
                throw new FileNotFoundException($"sing-box executable not found at {exePath}");

            if (!File.Exists(configPath))
                throw new FileNotFoundException($"Configuration file not found at {configPath}");

            _logger.Information("Starting sing-box: {Exe} run -c {Config}", exePath, configPath);

            _outputCts = new CancellationTokenSource();

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"run -c \"{configPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true, // Needed for graceful shutdown via stdin close
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += OnExited;

            _process.Start();

            _logger.Information("sing-box started with PID {Pid}", _process.Id);

            // Read stdout and stderr in background
            _ = ReadStreamAsync(_process.StandardOutput, "stdout", _outputCts.Token);
            _ = ReadStreamAsync(_process.StandardError, "stderr", _outputCts.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Stop ─────────────────────────────────────────────────────────────

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_process is null || !IsRunning)
            {
                _logger.Debug("sing-box is not running, nothing to stop");
                Cleanup();
                return;
            }

            var pid = _process.Id;
            _logger.Information("Stopping sing-box (PID {Pid})", pid);

            try
            {
                // 1. Attempt graceful shutdown: close stdin, sing-box may interpret as shutdown signal
                try
                {
                    _process.StandardInput.Close();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to close stdin for graceful shutdown");
                }

                // Wait briefly for graceful exit
                var exited = await WaitForExitAsync(_process, GracefulStopTimeoutMs);

                if (!exited)
                {
                    // 2. Force kill the process
                    _logger.Debug("sing-box did not exit gracefully within {Timeout}ms, killing", GracefulStopTimeoutMs);
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                        await WaitForExitAsync(_process, ForceKillTimeoutMs);
                    }
                    catch (InvalidOperationException)
                    {
                        // Process already exited between the check and kill
                    }
                }

                _logger.Information("sing-box (PID {Pid}) stopped", pid);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error stopping sing-box (PID {Pid})", pid);
            }
            finally
            {
                Cleanup();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Restart ───────────────────────────────────────────────────────────

    public async Task RestartAsync(string configPath)
    {
        _logger.Information("Restarting sing-box with config {Config}", configPath);
        await StopAsync();
        await StartAsync(configPath);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private string ResolveSingBoxPath()
    {
        var appDir = _platform.GetAppDirectory();
        return Path.Combine(appDir, AppDefaults.SingBoxExe);
    }

    private async Task ReadStreamAsync(StreamReader reader, string streamName, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;

                if (string.Equals(streamName, "stderr", StringComparison.Ordinal))
                    _logger.Warning("[sing-box] {Line}", line);
                else
                    _logger.Debug("[sing-box] {Line}", line);

                OnLogLine?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Stream reading ended ({Stream})", streamName);
        }
    }

    private async void OnExited(object? sender, EventArgs e)
    {
        try
        {
            // Brief delay to let stderr/stdout readers drain remaining output
            await Task.Delay(100);

            var exitCode = -1;
            try { exitCode = _process?.ExitCode ?? -1; }
            catch (InvalidOperationException) { /* process already disposed */ }

            _logger.Information("sing-box exited with code {ExitCode}", exitCode);
            OnProcessExited?.Invoke(exitCode);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unhandled exception in OnExited handler");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Cleanup()
    {
        _outputCts?.Cancel();
        _outputCts?.Dispose();
        _outputCts = null;

        if (_process is not null)
        {
            _process.Exited -= OnExited;
            _process.Dispose();
            _process = null;
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (IsRunning)
        {
            _logger.Debug("Disposing SingBoxProcessManager — killing running process");
            try
            {
                _process!.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort
            }
        }

        Cleanup();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
