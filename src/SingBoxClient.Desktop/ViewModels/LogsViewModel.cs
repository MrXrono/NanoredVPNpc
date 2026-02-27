using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ReactiveUI;
using Serilog;
using SingBoxClient.Core.Services;

namespace SingBoxClient.Desktop.ViewModels;

/// <summary>
/// ViewModel for the log viewer page — streams sing-box output and application log lines.
/// </summary>
public class LogsViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<LogsViewModel>();

    private readonly ILogService _logService;
    private readonly ISingBoxProcessManager _processManager;
    private readonly StringBuilder _logBuffer = new();
    private bool _disposed;

    private const int MaxLogLength = 500_000; // ~500 KB text cap

    // ── Properties ────────────────────────────────────────────────────────

    private string _logText = string.Empty;
    public string LogText
    {
        get => _logText;
        set => this.RaiseAndSetIfChanged(ref _logText, value);
    }

    private bool _autoScroll = true;
    public bool AutoScroll
    {
        get => _autoScroll;
        set => this.RaiseAndSetIfChanged(ref _autoScroll, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public LogsViewModel(
        ILogService logService,
        ISingBoxProcessManager processManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));

        ClearCommand = ReactiveCommand.Create(ClearLogs);
        CopyCommand = ReactiveCommand.CreateFromTask(CopyLogsToClipboardAsync);

        // Subscribe to sing-box process output lines
        _processManager.OnLogLine += OnProcessLogLine;

        // Subscribe to application log lines
        _logService.OnNewLogLine += OnAppLogLine;

        Logger.Debug("LogsViewModel initialized");
    }

    // ── Event Handlers ───────────────────────────────────────────────────

    private void OnProcessLogLine(string line)
    {
        AppendLine($"[sing-box] {line}");
    }

    private void OnAppLogLine(string line)
    {
        AppendLine(line);
    }

    private void AppendLine(string line)
    {
        // Marshal to UI thread via Avalonia Dispatcher
        Dispatcher.UIThread.Post(() => AppendLineCore(line));
    }

    private void AppendLineCore(string line)
    {
        _logBuffer.AppendLine(line);

        // Trim buffer if it exceeds the maximum length
        if (_logBuffer.Length > MaxLogLength)
        {
            var overflow = _logBuffer.Length - MaxLogLength;
            _logBuffer.Remove(0, overflow);
        }

        LogText = _logBuffer.ToString();
    }

    // ── Command Handlers ─────────────────────────────────────────────────

    private void ClearLogs()
    {
        try
        {
            _logBuffer.Clear();
            LogText = string.Empty;
            Logger.Debug("Log viewer cleared");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to clear logs");
        }
    }

    private async Task CopyLogsToClipboardAsync()
    {
        try
        {
            var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;

            var clipboard = mainWindow is not null
                ? TopLevel.GetTopLevel(mainWindow)?.Clipboard
                : null;

            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(LogText);
                Logger.Debug("Logs copied to clipboard ({Length} chars)", LogText.Length);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to copy logs to clipboard");
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _processManager.OnLogLine -= OnProcessLogLine;
        _logService.OnNewLogLine -= OnAppLogLine;

        GC.SuppressFinalize(this);
    }
}
