using System;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using Serilog;
using SingBoxClient.Core.Models;
using SingBoxClient.Core.Services;

namespace SingBoxClient.Desktop.ViewModels;

/// <summary>
/// Root ViewModel that owns navigation, global connection state, theme toggle, and update status.
/// </summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<MainViewModel>();

    private readonly ISettingsService _settingsService;
    private readonly IUpdateService _updateService;
    private readonly ISingBoxProcessManager _processManager;
    private readonly IConnectionGuardService _connectionGuard;
    private bool _disposed;

    // ── Child ViewModels (navigation targets) ────────────────────────────

    public HomeViewModel HomeViewModel { get; }
    public RoutingViewModel RoutingViewModel { get; }
    public TunSettingsViewModel TunSettingsViewModel { get; }
    public LogsViewModel LogsViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }

    // ── Properties ────────────────────────────────────────────────────────

    private ViewModelBase _currentPage = null!;
    public ViewModelBase CurrentPage
    {
        get => _currentPage;
        set => this.RaiseAndSetIfChanged(ref _currentPage, value);
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;
    public ConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set => this.RaiseAndSetIfChanged(ref _isUpdateAvailable, value);
    }

    private UpdateInfo? _pendingUpdate;
    public UpdateInfo? PendingUpdate
    {
        get => _pendingUpdate;
        set => this.RaiseAndSetIfChanged(ref _pendingUpdate, value);
    }

    private bool _isDarkTheme = true;
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => this.RaiseAndSetIfChanged(ref _isDarkTheme, value);
    }

    private bool _isNotNavigating = true;
    public bool IsNotNavigating
    {
        get => _isNotNavigating;
        set => this.RaiseAndSetIfChanged(ref _isNotNavigating, value);
    }

    // ── Events (window chrome actions) ──────────────────────────────────

    public event Action? OnMinimizeRequested;
    public event Action? OnMaximizeRequested;
    public event Action? OnCloseRequested;

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<string, Unit> NavigateCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> MinimizeCommand { get; }
    public ReactiveCommand<Unit, Unit> MaximizeCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public MainViewModel(
        ISettingsService settingsService,
        IUpdateService updateService,
        ISingBoxProcessManager processManager,
        IConnectionGuardService connectionGuardService,
        HomeViewModel homeViewModel,
        RoutingViewModel routingViewModel,
        TunSettingsViewModel tunSettingsViewModel,
        LogsViewModel logsViewModel,
        SettingsViewModel settingsViewModel)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _connectionGuard = connectionGuardService ?? throw new ArgumentNullException(nameof(connectionGuardService));

        HomeViewModel = homeViewModel ?? throw new ArgumentNullException(nameof(homeViewModel));
        RoutingViewModel = routingViewModel ?? throw new ArgumentNullException(nameof(routingViewModel));
        TunSettingsViewModel = tunSettingsViewModel ?? throw new ArgumentNullException(nameof(tunSettingsViewModel));
        LogsViewModel = logsViewModel ?? throw new ArgumentNullException(nameof(logsViewModel));
        SettingsViewModel = settingsViewModel ?? throw new ArgumentNullException(nameof(settingsViewModel));

        // Commands
        NavigateCommand = ReactiveCommand.Create<string>(NavigateTo);
        ToggleThemeCommand = ReactiveCommand.Create(ToggleTheme);
        MinimizeCommand = ReactiveCommand.Create(() => OnMinimizeRequested?.Invoke());
        MaximizeCommand = ReactiveCommand.Create(() => OnMaximizeRequested?.Invoke());
        CloseCommand = ReactiveCommand.Create(() => OnCloseRequested?.Invoke());

        // Set initial page
        CurrentPage = HomeViewModel;

        // Load theme from settings
        IsDarkTheme = _settingsService.Current.Theme == "dark";

        // Subscribe to connection status changes
        _connectionGuard.OnStatusChanged += OnConnectionStatusChanged;

        // Check for updates on startup
        _ = CheckForUpdatesAsync();

        Logger.Information("MainViewModel initialized, default page: Home");
    }

    // ── Navigation ───────────────────────────────────────────────────────

    private void NavigateTo(string pageName)
    {
        try
        {
            CurrentPage = pageName.ToLowerInvariant() switch
            {
                "home" => HomeViewModel,
                "routing" => RoutingViewModel,
                "tun" or "tunsettings" => TunSettingsViewModel,
                "logs" => LogsViewModel,
                "settings" => SettingsViewModel,
                _ => HomeViewModel
            };

            Logger.Debug("Navigated to {Page}", pageName);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Navigation failed for page {Page}", pageName);
        }
    }

    // ── Theme ────────────────────────────────────────────────────────────

    private void ToggleTheme()
    {
        try
        {
            IsDarkTheme = !IsDarkTheme;
            _settingsService.Current.Theme = IsDarkTheme ? "dark" : "light";
            _settingsService.Save();

            if (Avalonia.Application.Current is App app)
                app.ApplyTheme(IsDarkTheme);

            Logger.Information("Theme toggled to {Theme}", IsDarkTheme ? "dark" : "light");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to toggle theme");
        }
    }

    // ── Connection Guard ─────────────────────────────────────────────────

    private void OnConnectionStatusChanged(ConnectionStatus status)
    {
        ConnectionStatus = status;
        IsConnected = status == ConnectionStatus.Connected;

        Logger.Debug("Connection status changed: {Status}", status);
    }

    // ── Update Check ─────────────────────────────────────────────────────

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update is not null && update.Available)
            {
                PendingUpdate = update;
                IsUpdateAvailable = true;
                Logger.Information("Update available: v{Version}", update.Version);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to check for updates on startup");
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _connectionGuard.OnStatusChanged -= OnConnectionStatusChanged;

        GC.SuppressFinalize(this);
    }
}
