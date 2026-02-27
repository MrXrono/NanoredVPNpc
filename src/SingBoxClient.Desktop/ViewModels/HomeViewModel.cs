using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using Avalonia.Threading;
using ReactiveUI;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Models;
using SingBoxClient.Core.Platform;
using SingBoxClient.Core.Services;

namespace SingBoxClient.Desktop.ViewModels;

/// <summary>
/// ViewModel for the home / dashboard page — connection toggle, server selection, speed stats, timer.
/// </summary>
public class HomeViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<HomeViewModel>();

    private readonly ISettingsService _settingsService;
    private readonly ISingBoxProcessManager _processManager;
    private readonly IClashApiClient _clashApi;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ICountryGroupingService _countryGroupingService;
    private readonly IPingService _pingService;
    private readonly IConnectionGuardService _connectionGuard;
    private readonly IRoutingService _routingService;
    private readonly IRemoteConfigService _remoteConfigService;
    private readonly IPlatformService _platformService;
    private readonly IAnalyticsService _analyticsService;
    private readonly IAnnouncementService _announcementService;
    private readonly ISingBoxConfigBuilder _configBuilder;

    private DispatcherTimer? _connectionTimer;
    private DispatcherTimer? _trafficTimer;
    private DateTime _connectedSince;
    private CancellationTokenSource? _trafficCts;
    private bool _disposed;

    // ── Properties ────────────────────────────────────────────────────────

    private bool _isProxyEnabled = true;
    public bool IsProxyEnabled
    {
        get => _isProxyEnabled;
        set
        {
            // Don't allow unchecking if TUN is also off — at least one mode must be active
            if (!value && !IsTunEnabled)
            {
                this.RaisePropertyChanged(nameof(IsProxyEnabled));
                return;
            }

            this.RaiseAndSetIfChanged(ref _isProxyEnabled, value);
            if (value) IsTunEnabled = false;
        }
    }

    private bool _isTunEnabled;
    public bool IsTunEnabled
    {
        get => _isTunEnabled;
        set
        {
            // Don't allow unchecking if Proxy is also off — at least one mode must be active
            if (!value && !IsProxyEnabled)
            {
                this.RaisePropertyChanged(nameof(IsTunEnabled));
                return;
            }

            this.RaiseAndSetIfChanged(ref _isTunEnabled, value);
            if (value) IsProxyEnabled = false;
        }
    }

    private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;
    public ConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _connectionStatus, value);
            this.RaisePropertyChanged(nameof(Status));
            this.RaisePropertyChanged(nameof(ConnectButtonText));
        }
    }

    /// <summary>
    /// String representation of the current connection status, derived from the enum.
    /// Bound by the view for the status indicator color converter.
    /// </summary>
    public ConnectionStatus Status => ConnectionStatus;

    private string _statusText = "Disconnected";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private string _connectedCountry = string.Empty;
    public string ConnectedCountry
    {
        get => _connectedCountry;
        set => this.RaiseAndSetIfChanged(ref _connectedCountry, value);
    }

    private string _timer = "00:00:00";
    public string Timer
    {
        get => _timer;
        set => this.RaiseAndSetIfChanged(ref _timer, value);
    }

    private string _uploadSpeed = "0 B/s";
    public string UploadSpeed
    {
        get => _uploadSpeed;
        set => this.RaiseAndSetIfChanged(ref _uploadSpeed, value);
    }

    private string _downloadSpeed = "0 B/s";
    public string DownloadSpeed
    {
        get => _downloadSpeed;
        set => this.RaiseAndSetIfChanged(ref _downloadSpeed, value);
    }

    private ObservableCollection<CountryGroup> _countries = new();
    public ObservableCollection<CountryGroup> Countries
    {
        get => _countries;
        set => this.RaiseAndSetIfChanged(ref _countries, value);
    }

    private CountryGroup? _selectedCountry;
    public CountryGroup? SelectedCountry
    {
        get => _selectedCountry;
        set => this.RaiseAndSetIfChanged(ref _selectedCountry, value);
    }

    private SubscriptionData? _subscriptionInfo;
    public SubscriptionData? SubscriptionInfo
    {
        get => _subscriptionInfo;
        set
        {
            this.RaiseAndSetIfChanged(ref _subscriptionInfo, value);
            this.RaisePropertyChanged(nameof(SubscriptionId));
            this.RaisePropertyChanged(nameof(ExpiresAt));
            this.RaisePropertyChanged(nameof(TrafficUsageText));
            this.RaisePropertyChanged(nameof(TrafficPercent));
        }
    }

    /// <summary>Subscription ID extracted from SubscriptionInfo, or "—" if unavailable.</summary>
    public string SubscriptionId => SubscriptionInfo?.Id ?? "—";

    /// <summary>Formatted expiration date from SubscriptionInfo, or "—" if unavailable.</summary>
    public string ExpiresAt => SubscriptionInfo is not null
        ? SubscriptionInfo.ExpiresAt.ToString("yyyy-MM-dd")
        : "—";

    /// <summary>Human-readable traffic usage, e.g. "1.2 GB / 50.0 GB".</summary>
    public string TrafficUsageText
    {
        get
        {
            if (SubscriptionInfo is null) return "— / —";
            return $"{BytesToGb(SubscriptionInfo.UsedTraffic)} / {BytesToGb(SubscriptionInfo.TotalTraffic)}";
        }
    }

    /// <summary>Traffic usage as a percentage (0–100) for the progress bar.</summary>
    public double TrafficPercent
    {
        get
        {
            if (SubscriptionInfo is null || SubscriptionInfo.TotalTraffic <= 0) return 0;
            var pct = (double)SubscriptionInfo.UsedTraffic / SubscriptionInfo.TotalTraffic * 100;
            return Math.Clamp(pct, 0, 100);
        }
    }

    private ObservableCollection<Announcement> _announcements = new();
    public ObservableCollection<Announcement> Announcements
    {
        get => _announcements;
        set
        {
            this.RaiseAndSetIfChanged(ref _announcements, value);
            this.RaisePropertyChanged(nameof(HasAnnouncements));
        }
    }

    /// <summary>True if there are any unread announcements to display.</summary>
    public bool HasAnnouncements => Announcements.Count > 0;

    private bool _isConnecting;
    public bool IsConnecting
    {
        get => _isConnecting;
        set => this.RaiseAndSetIfChanged(ref _isConnecting, value);
    }

    /// <summary>Button label: "Connect" when disconnected, "Disconnect" when connected.</summary>
    public string ConnectButtonText => ConnectionStatus == ConnectionStatus.Connected
                                     || ConnectionStatus == ConnectionStatus.Reconnecting
        ? "Disconnect"
        : "Connect";

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshServersCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> RenewCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public HomeViewModel(
        ISettingsService settingsService,
        ISingBoxProcessManager processManager,
        IClashApiClient clashApi,
        ISubscriptionService subscriptionService,
        ICountryGroupingService countryGroupingService,
        IPingService pingService,
        IConnectionGuardService connectionGuard,
        IRoutingService routingService,
        IRemoteConfigService remoteConfigService,
        IPlatformService platformService,
        IAnalyticsService analyticsService,
        IAnnouncementService announcementService,
        ISingBoxConfigBuilder configBuilder)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _clashApi = clashApi ?? throw new ArgumentNullException(nameof(clashApi));
        _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
        _countryGroupingService = countryGroupingService ?? throw new ArgumentNullException(nameof(countryGroupingService));
        _pingService = pingService ?? throw new ArgumentNullException(nameof(pingService));
        _connectionGuard = connectionGuard ?? throw new ArgumentNullException(nameof(connectionGuard));
        _routingService = routingService ?? throw new ArgumentNullException(nameof(routingService));
        _remoteConfigService = remoteConfigService ?? throw new ArgumentNullException(nameof(remoteConfigService));
        _platformService = platformService ?? throw new ArgumentNullException(nameof(platformService));
        _analyticsService = analyticsService ?? throw new ArgumentNullException(nameof(analyticsService));
        _announcementService = announcementService ?? throw new ArgumentNullException(nameof(announcementService));
        _configBuilder = configBuilder ?? throw new ArgumentNullException(nameof(configBuilder));

        // Load mode from settings
        IsProxyEnabled = _settingsService.Current.ProxyEnabled;
        IsTunEnabled = _settingsService.Current.TunEnabled;

        // Connect command is disabled while already connecting
        var canConnect = this.WhenAnyValue(
            x => x.IsConnecting,
            x => x.ConnectionStatus,
            (connecting, status) => !connecting && status != ConnectionStatus.Connected);

        var canDisconnect = this.WhenAnyValue(
            x => x.ConnectionStatus,
            status => status == ConnectionStatus.Connected || status == ConnectionStatus.Reconnecting);

        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync, canConnect);
        DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectAsync, canDisconnect);
        RefreshServersCommand = ReactiveCommand.CreateFromTask(RefreshServersAsync);

        // Toggle command: routes to Connect or Disconnect based on current state
        var canToggle = this.WhenAnyValue(x => x.IsConnecting, connecting => !connecting);
        ToggleConnectionCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (ConnectionStatus == ConnectionStatus.Connected
                || ConnectionStatus == ConnectionStatus.Reconnecting)
            {
                await DisconnectAsync();
            }
            else
            {
                await ConnectAsync();
            }
        }, canToggle);

        // Renew command: placeholder for future subscription renewal flow
        RenewCommand = ReactiveCommand.Create(() => { });

        // Subscribe to connection guard status changes
        _connectionGuard.OnStatusChanged += OnConnectionStatusChanged;

        // Load announcements
        LoadAnnouncements();

        Logger.Information("HomeViewModel initialized");
    }

    // ── Connect ──────────────────────────────────────────────────────────

    private async Task ConnectAsync()
    {
        IsConnecting = true;
        ConnectionStatus = ConnectionStatus.Connecting;
        StatusText = "Connecting...";

        try
        {
            // 1. Determine the best server from the selected country
            if (SelectedCountry is null)
            {
                Logger.Warning("No country selected, cannot connect");
                StatusText = "Select a server";
                ConnectionStatus = ConnectionStatus.Disconnected;
                return;
            }

            var bestServer = SelectedCountry.BestServer;
            if (bestServer is null)
            {
                // Try pinging to find the best one
                bestServer = await _pingService.GetBestServerAsync(SelectedCountry);
            }

            if (bestServer is null)
            {
                Logger.Error("No reachable server found in {Country}", SelectedCountry.DisplayName);
                StatusText = "No reachable server";
                ConnectionStatus = ConnectionStatus.Error;
                return;
            }

            Logger.Information("Connecting to {Server} in {Country}",
                bestServer.Name, SelectedCountry.DisplayName);

            // 2. Sync current mode toggles into settings before config build
            _settingsService.Current.ProxyEnabled = IsProxyEnabled;
            _settingsService.Current.TunEnabled = IsTunEnabled;

            // 3. Build sing-box configuration (reads settings + routing rules internally)
            var configPath = _configBuilder.BuildAndSave(bestServer);

            // 4. Start sing-box
            await _processManager.StartAsync(configPath);

            // 5. If proxy mode, set system proxy
            if (IsProxyEnabled)
            {
                _platformService.SetSystemProxy("127.0.0.1", _settingsService.Current.ProxyPort);
                Logger.Debug("System proxy set to 127.0.0.1:{Port}", _settingsService.Current.ProxyPort);
            }

            // 6. Start connection guard monitoring
            _connectionGuard.StartMonitoring(SelectedCountry, bestServer);

            // 7. Start the connection timer
            _connectedSince = DateTime.UtcNow;
            StartConnectionTimer();

            // 8. Start traffic speed polling
            StartTrafficPolling();

            // 9. Track analytics event
            _ = _analyticsService.TrackAsync(new AnalyticsEvent
            {
                EventName = "connect",
                Properties = new Dictionary<string, string>
                {
                    ["country"] = SelectedCountry.Code,
                    ["server"] = bestServer.Name,
                    ["protocol"] = bestServer.Protocol.ToString(),
                    ["mode"] = IsProxyEnabled ? "proxy" : "tun"
                }
            });

            // Update state
            ConnectionStatus = ConnectionStatus.Connected;
            StatusText = "Connected";
            ConnectedCountry = SelectedCountry.DisplayName;

            // Persist mode to settings
            _settingsService.Current.ProxyEnabled = IsProxyEnabled;
            _settingsService.Current.TunEnabled = IsTunEnabled;
            _settingsService.Current.SelectedCountry = SelectedCountry.Code;
            _settingsService.Save();

            Logger.Information("Successfully connected to {Server} ({Country})",
                bestServer.Name, SelectedCountry.DisplayName);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Connection failed");
            ConnectionStatus = ConnectionStatus.Error;
            StatusText = $"Error: {ex.Message}";

            // Cleanup on failure
            try { await _processManager.StopAsync(); } catch { /* best effort */ }
            try { _platformService.ClearSystemProxy(); } catch { /* best effort */ }
        }
        finally
        {
            IsConnecting = false;
        }
    }

    // ── Disconnect ───────────────────────────────────────────────────────

    private async Task DisconnectAsync()
    {
        try
        {
            Logger.Information("Disconnecting...");
            StatusText = "Disconnecting...";

            // 1. Stop traffic polling
            StopTrafficPolling();

            // 2. Stop connection timer
            StopConnectionTimer();

            // 3. Stop connection guard
            _connectionGuard.StopMonitoring();

            // 4. Clear system proxy
            _platformService.ClearSystemProxy();

            // 5. Close all proxy connections
            try { await _clashApi.CloseAllConnectionsAsync(); } catch { /* best effort */ }

            // 6. Stop sing-box
            await _processManager.StopAsync();

            // 7. Track analytics
            _ = _analyticsService.TrackAsync(new AnalyticsEvent
            {
                EventName = "disconnect",
                Properties = new Dictionary<string, string>
                {
                    ["duration"] = (DateTime.UtcNow - _connectedSince).TotalSeconds.ToString("F0")
                }
            });

            // Reset state
            ConnectionStatus = ConnectionStatus.Disconnected;
            StatusText = "Disconnected";
            ConnectedCountry = string.Empty;
            Timer = "00:00:00";
            UploadSpeed = "0 B/s";
            DownloadSpeed = "0 B/s";

            Logger.Information("Disconnected successfully");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during disconnect");
            ConnectionStatus = ConnectionStatus.Error;
            StatusText = $"Error: {ex.Message}";
        }
    }

    // ── Refresh Servers ──────────────────────────────────────────────────

    private async Task RefreshServersAsync()
    {
        try
        {
            var subUrl = _settingsService.Current.SubscriptionUrl;
            if (string.IsNullOrWhiteSpace(subUrl))
            {
                Logger.Warning("No subscription URL configured");
                return;
            }

            Logger.Information("Refreshing servers from subscription...");

            // 1. Fetch subscription
            var servers = await _subscriptionService.FetchAndParseAsync(subUrl);
            if (servers.Count == 0)
            {
                Logger.Warning("Subscription returned 0 servers");
                return;
            }

            // 2. Group by country
            var groups = _countryGroupingService.GroupByCountry(servers);

            // 3. Ping all servers for latency measurement
            await _pingService.PingAllAsync(servers);

            // 4. Update best server per group
            foreach (var group in groups)
            {
                group.BestServer = group.Servers
                    .Where(s => s.IsReachable)
                    .OrderBy(s => s.Latency)
                    .FirstOrDefault();

                group.AverageLatency = (int)Math.Round(
                    group.Servers
                        .Where(s => s.IsReachable && s.Latency >= 0)
                        .Select(s => s.Latency)
                        .DefaultIfEmpty(0)
                        .Average());
            }

            Countries = new ObservableCollection<CountryGroup>(
                groups.OrderBy(g => g.AverageLatency));

            // Restore previously selected country
            var selectedCode = _settingsService.Current.SelectedCountry;
            if (!string.IsNullOrEmpty(selectedCode))
            {
                SelectedCountry = Countries.FirstOrDefault(c => c.Code == selectedCode);
            }

            SelectedCountry ??= Countries.FirstOrDefault();

            Logger.Information("Refreshed {Count} servers in {Groups} countries",
                servers.Count, groups.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to refresh servers");
        }
    }

    // ── Connection Timer ─────────────────────────────────────────────────

    private void StartConnectionTimer()
    {
        StopConnectionTimer();

        _connectionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _connectionTimer.Tick += OnTimerTick;
        _connectionTimer.Start();
    }

    private void StopConnectionTimer()
    {
        if (_connectionTimer is not null)
        {
            _connectionTimer.Tick -= OnTimerTick;
            _connectionTimer.Stop();
            _connectionTimer = null;
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _connectedSince;
        Timer = elapsed.ToString(@"hh\:mm\:ss");
    }

    // ── Traffic Speed Polling ────────────────────────────────────────────

    private void StartTrafficPolling()
    {
        StopTrafficPolling();

        _trafficCts = new CancellationTokenSource();

        _trafficTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _trafficTimer.Tick += OnTrafficTick;
        _trafficTimer.Start();
    }

    private void StopTrafficPolling()
    {
        _trafficCts?.Cancel();
        _trafficCts?.Dispose();
        _trafficCts = null;

        if (_trafficTimer is not null)
        {
            _trafficTimer.Tick -= OnTrafficTick;
            _trafficTimer.Stop();
            _trafficTimer = null;
        }
    }

    private async void OnTrafficTick(object? sender, EventArgs e)
    {
        try
        {
            var stats = await _clashApi.GetTrafficAsync();
            UploadSpeed = BytesToHumanSpeed(stats.UploadSpeed);
            DownloadSpeed = BytesToHumanSpeed(stats.DownloadSpeed);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to fetch traffic stats");
        }
    }

    // ── Connection Guard Handler ─────────────────────────────────────────

    private void OnConnectionStatusChanged(ConnectionStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = status;
            StatusText = status switch
            {
                ConnectionStatus.Connected => "Connected",
                ConnectionStatus.Connecting => "Connecting...",
                ConnectionStatus.Reconnecting => "Reconnecting...",
                ConnectionStatus.Disconnecting => "Disconnecting...",
                ConnectionStatus.Disconnected => "Disconnected",
                ConnectionStatus.Error => "Connection lost",
                _ => status.ToString()
            };

            if (status == ConnectionStatus.Disconnected)
            {
                StopConnectionTimer();
                StopTrafficPolling();
                Timer = "00:00:00";
                UploadSpeed = "0 B/s";
                DownloadSpeed = "0 B/s";
                ConnectedCountry = string.Empty;

                try { _platformService.ClearSystemProxy(); } catch { /* best effort */ }
            }
        });
    }

    // ── Announcements ────────────────────────────────────────────────────

    private void LoadAnnouncements()
    {
        try
        {
            var items = _announcementService.GetAll()
                .Where(a => !a.IsRead)
                .OrderByDescending(a => a.CreatedAt)
                .ToList();

            Announcements = new ObservableCollection<Announcement>(items);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load announcements");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Convert a byte count to a human-readable GB string (e.g. "12.3 GB").
    /// </summary>
    private static string BytesToGb(long bytes)
    {
        var gb = bytes / (1024.0 * 1024 * 1024);
        return $"{gb:F1} GB";
    }

    /// <summary>
    /// Convert bytes per second to a human-readable speed string.
    /// </summary>
    private static string BytesToHumanSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 B/s";

        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        double value = bytesPerSecond;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:F1} {units[unitIndex]}";
    }

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _connectionGuard.OnStatusChanged -= OnConnectionStatusChanged;
        StopConnectionTimer();
        StopTrafficPolling();

        GC.SuppressFinalize(this);
    }
}
