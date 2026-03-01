using System;
using System.Threading;
using Avalonia.Threading;
using Serilog;

namespace SingBoxClient.Desktop.Services;

/// <summary>
/// Background watchdog that monitors UI thread responsiveness.
/// If the UI thread does not respond within the configured timeout,
/// a FATAL log entry is written from the watchdog thread (which is still alive)
/// and flushed to disk immediately.
/// </summary>
public sealed class UiWatchdogService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<UiWatchdogService>();

    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _hangTimeout;
    private Timer? _timer;
    private volatile bool _disposed;

    /// <param name="checkInterval">How often to check UI thread (default: 10s).</param>
    /// <param name="hangTimeout">Max time to wait for UI thread response (default: 15s).</param>
    public UiWatchdogService(TimeSpan? checkInterval = null, TimeSpan? hangTimeout = null)
    {
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(10);
        _hangTimeout = hangTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Starts the watchdog. Must be called after Avalonia's UI thread is running.
    /// </summary>
    public void Start()
    {
        _timer = new Timer(CheckUiThread, null, _checkInterval, _checkInterval);
        Logger.Debug("UI watchdog started (interval={Interval}s, timeout={Timeout}s)",
            _checkInterval.TotalSeconds, _hangTimeout.TotalSeconds);
    }

    private void CheckUiThread(object? state)
    {
        if (_disposed) return;

        using var signal = new ManualResetEventSlim(false);

        try
        {
            Dispatcher.UIThread.Post(() => signal.Set(), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex, "UI WATCHDOG: Failed to post to UI thread — dispatcher may be dead");
            Log.CloseAndFlush();
            return;
        }

        if (!signal.Wait(_hangTimeout))
        {
            Logger.Fatal(
                "UI WATCHDOG: UI thread is NOT RESPONDING for over {Timeout} seconds. " +
                "Possible deadlock or infinite loop on the UI thread",
                _hangTimeout.TotalSeconds);
            Log.CloseAndFlush();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
