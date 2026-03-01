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

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private DispatcherTimer? _connectionTimer;
    private DateTime _connectedSince;
    private CancellationTokenSource? _trafficCts;
    private string? _lastSubscriptionUrl;
    private ServerNode? _connectedServer;
    private bool _disposed;

    // ── Properties ────────────────────────────────────────────────────────

    private bool _isProxyEnabled = true;
    public bool IsProxyEnabled
    {
        get => _isProxyEnabled;
        set
        {
            if (_isProxyEnabled == value) return;
            this.RaiseAndSetIfChanged(ref _isProxyEnabled, value);
            _ = ApplyModeChangeAsync();
        }
    }

    private bool _isTunEnabled;
    public bool IsTunEnabled
    {
        get => _isTunEnabled;
        set
        {
            if (_isTunEnabled == value) return;
            this.RaiseAndSetIfChanged(ref _isTunEnabled, value);
            _ = ApplyModeChangeAsync();
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

    private static string L(string key)
    {
        if (Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var val) == true && val is string s)
            return s;
        return key;
    }

    private string _statusText = L("Disconnected");
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

    private long _sessionUploadBytes;
    private long _sessionDownloadBytes;

    private string _sessionUpload = "0 B";
    public string SessionUpload
    {
        get => _sessionUpload;
        set => this.RaiseAndSetIfChanged(ref _sessionUpload, value);
    }

    private string _sessionDownload = "0 B";
    public string SessionDownload
    {
        get => _sessionDownload;
        set => this.RaiseAndSetIfChanged(ref _sessionDownload, value);
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

    /// <summary>Formatted expiration info: "14 дней (14-03-2026)" or "—" if unavailable.</summary>
    public string ExpiresAt
    {
        get
        {
            if (SubscriptionInfo?.ExpiresAt is not { } expires)
                return "—";

            var daysLeft = (int)Math.Ceiling((expires - DateTime.UtcNow).TotalDays);
            if (daysLeft < 0) daysLeft = 0;

            var lang = _settingsService.Current.Language;
            var daysWord = lang == "ru" ? PluralizeRu(daysLeft, "день", "дня", "дней") : (daysLeft == 1 ? "day" : "days");
            var dateStr = expires.ToString("dd-MM-yyyy");

            return $"{daysLeft} {daysWord}  ({dateStr})";
        }
    }

    private static string PluralizeRu(int n, string one, string few, string many)
    {
        var abs = Math.Abs(n) % 100;
        if (abs is >= 11 and <= 19) return many;
        var last = abs % 10;
        return last switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many
        };
    }

    /// <summary>Human-readable traffic usage, e.g. "1.2 GB / 50.0 GB" or "44.8 MB / Unlimited".</summary>
    public string TrafficUsageText
    {
        get
        {
            if (SubscriptionInfo is null) return "— / —";
            var used = BytesToHumanSize(SubscriptionInfo.UsedTraffic);
            if (SubscriptionInfo.IsUnlimitedTraffic)
                return $"{used} / {L("Unlimited")}";
            return $"{used} / {BytesToHumanSize(SubscriptionInfo.TotalTraffic)}";
        }
    }

    /// <summary>Traffic usage as a percentage (0–100) for the progress bar. 0 for unlimited plans.</summary>
    public double TrafficPercent
    {
        get
        {
            if (SubscriptionInfo is null || SubscriptionInfo.IsUnlimitedTraffic) return 0;
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
        ? L("Disconnect")
        : L("Connect");

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

        // Renew command: opens support URL or subscription management page
        RenewCommand = ReactiveCommand.Create(OpenRenewUrl);

        // Subscribe to connection guard status changes
        _connectionGuard.OnStatusChanged += OnConnectionStatusChanged;

        // Refresh servers when subscription URL changes in settings
        _lastSubscriptionUrl = _settingsService.Current.SubscriptionUrl;
        _settingsService.OnSettingsChanged += OnSettingsChanged;

        // Load announcements
        LoadAnnouncements();

        // Auto-load subscription on startup
        _ = RefreshServersAsync();

        Logger.Information("HomeViewModel initialized");
    }

    // ── Connect ──────────────────────────────────────────────────────────

    private async Task ConnectAsync()
    {
        if (!await _connectGate.WaitAsync(0))
        {
            Logger.Debug("Connect/Disconnect already in progress, ignoring");
            return;
        }

        try
        {
            IsConnecting = true;
            ConnectionStatus = ConnectionStatus.Connecting;
            StatusText = L("Connecting");

            // 0. Check subscription expiration
            if (SubscriptionInfo is { IsExpired: true })
            {
                Logger.Warning("Subscription expired at {ExpiresAt}", SubscriptionInfo.ExpiresAt);
                StatusText = L("SubscriptionExpired");
                ConnectionStatus = ConnectionStatus.Error;
                return;
            }

            // 1. Determine the best server from the selected country
            if (SelectedCountry is null)
            {
                Logger.Warning("No country selected, cannot connect");
                StatusText = L("SelectServer");
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
                StatusText = L("ConnectionError");
                ConnectionStatus = ConnectionStatus.Error;
                return;
            }

            Logger.Information("Connecting to {Server} in {Country}",
                bestServer.Name, SelectedCountry.DisplayName);

            _connectedServer = bestServer;

            // 2. Sync current mode toggles into settings before config build
            _settingsService.Current.ProxyEnabled = IsProxyEnabled;
            _settingsService.Current.TunEnabled = IsTunEnabled;

            // 3. Build sing-box configuration (reads settings + routing rules internally)
            var configPath = _configBuilder.BuildAndSave(bestServer);

            // 4. Start sing-box
            await _processManager.StartAsync(configPath);

            // 5. Wait for Clash API to become available before proceeding
            var apiReady = await WaitForClashApiAsync(TimeSpan.FromSeconds(10));
            if (!apiReady)
            {
                if (!_processManager.IsRunning)
                {
                    Logger.Error("sing-box process has exited unexpectedly after start");
                    throw new InvalidOperationException("sing-box crashed during startup — check logs for details");
                }

                Logger.Warning("Clash API did not become available, proceeding anyway");
            }

            // 6. If proxy mode, set system proxy
            if (IsProxyEnabled)
            {
                _platformService.SetSystemProxy("127.0.0.1", _settingsService.Current.ProxyPort);
                Logger.Debug("System proxy set to 127.0.0.1:{Port}", _settingsService.Current.ProxyPort);
            }

            // 7. Start connection guard monitoring
            _connectionGuard.StartMonitoring(SelectedCountry, bestServer);

            // 8. Start the connection timer
            _connectedSince = DateTime.UtcNow;
            StartConnectionTimer();

            // 9. Start traffic speed streaming
            StartTrafficStreaming();

            // 10. Track analytics event
            _ = _analyticsService.TrackAsync(new AnalyticsEvent
            {
                EventName = "connect",
                Properties = new Dictionary<string, string>
                {
                    ["country"] = SelectedCountry.Code,
                    ["server"] = bestServer.Name,
                    ["protocol"] = bestServer.Protocol.ToString(),
                    ["mode"] = IsProxyEnabled && IsTunEnabled ? "mixed"
                              : IsProxyEnabled ? "proxy"
                              : IsTunEnabled ? "tun"
                              : "none"
                }
            });

            // Update state
            ConnectionStatus = ConnectionStatus.Connected;
            StatusText = L("Connected");
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
            _connectGate.Release();
        }
    }

    // ── Disconnect ───────────────────────────────────────────────────────

    private async Task DisconnectAsync()
    {
        if (!await _connectGate.WaitAsync(0))
        {
            Logger.Debug("Connect/Disconnect already in progress, ignoring");
            return;
        }

        try
        {
            Logger.Information("Disconnecting...");
            StatusText = L("Disconnecting");

            // 1. Stop traffic streaming
            StopTrafficStreaming();

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
            _connectedServer = null;
            ConnectionStatus = ConnectionStatus.Disconnected;
            StatusText = L("Disconnected");
            ConnectedCountry = string.Empty;
            Timer = "00:00:00";
            UploadSpeed = "0 B/s";
            DownloadSpeed = "0 B/s";
            _sessionUploadBytes = 0;
            _sessionDownloadBytes = 0;
            SessionUpload = "0 B";
            SessionDownload = "0 B";

            Logger.Information("Disconnected successfully");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during disconnect");
            ConnectionStatus = ConnectionStatus.Error;
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // ── Live Mode Switching ─────────────────────────────────────────────

    /// <summary>
    /// Rebuilds sing-box config and restarts the process to apply mode changes
    /// while the connection is active. Also manages the system proxy setting.
    /// </summary>
    private async Task ApplyModeChangeAsync()
    {
        if (ConnectionStatus != ConnectionStatus.Connected
            && ConnectionStatus != ConnectionStatus.Reconnecting)
            return;

        if (_connectedServer is null)
            return;

        Logger.Information("Applying mode change: proxy={Proxy}, tun={Tun}", IsProxyEnabled, IsTunEnabled);

        try
        {
            // 1. Stop traffic streaming during restart
            StopTrafficStreaming();

            // 2. Sync mode flags to settings
            _settingsService.Current.ProxyEnabled = IsProxyEnabled;
            _settingsService.Current.TunEnabled = IsTunEnabled;
            _settingsService.Save();

            // 3. Rebuild config and restart sing-box
            var configPath = _configBuilder.BuildAndSave(_connectedServer);
            await _processManager.RestartAsync(configPath);

            // 4. Wait for Clash API
            await WaitForClashApiAsync(TimeSpan.FromSeconds(10));

            // 5. Manage system proxy
            if (IsProxyEnabled)
            {
                _platformService.SetSystemProxy("127.0.0.1", _settingsService.Current.ProxyPort);
                Logger.Debug("System proxy enabled");
            }
            else
            {
                _platformService.ClearSystemProxy();
                Logger.Debug("System proxy cleared");
            }

            // 6. Resume traffic streaming
            StartTrafficStreaming();

            Logger.Information("Mode change applied successfully");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to apply mode change");
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

            // 1. Fetch subscription with metadata
            var (servers, info) = await _subscriptionService.FetchWithInfoAsync(subUrl);

            // 2. Update subscription info panel
            if (info is not null)
            {
                SubscriptionInfo = info;
                Logger.Information("Subscription: {Title}, expires {Expires}, traffic {Used}/{Total}",
                    info.ProfileTitle, info.ExpiresAt, info.UsedTraffic, info.TotalTraffic);
            }

            if (servers.Count == 0)
            {
                Logger.Warning("Subscription returned 0 servers");
                return;
            }

            // 3. Group by country
            var groups = _countryGroupingService.GroupByCountry(servers);

            // 4. Ping all servers for latency measurement
            await _pingService.PingAllAsync(servers);

            // 5. Update best server per group
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

    // ── Traffic Streaming ──────────────────────────────────────────────

    private void StartTrafficStreaming()
    {
        StopTrafficStreaming();

        _trafficCts = new CancellationTokenSource();
        var ct = _trafficCts.Token;

        // Fire-and-forget: StreamTrafficAsync runs until ct is cancelled.
        // UI updates are dispatched to the Avalonia UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                await _clashApi.StreamTrafficAsync(stats =>
                {
                    _sessionUploadBytes += stats.UploadSpeed;
                    _sessionDownloadBytes += stats.DownloadSpeed;

                    Dispatcher.UIThread.Post(() =>
                    {
                        UploadSpeed = BytesToHumanSpeed(stats.UploadSpeed);
                        DownloadSpeed = BytesToHumanSpeed(stats.DownloadSpeed);
                        SessionUpload = BytesToHumanSize(_sessionUploadBytes);
                        SessionDownload = BytesToHumanSize(_sessionDownloadBytes);
                    });
                }, ct);
            }
            catch (OperationCanceledException)
            {
                // Expected on disconnect
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Traffic streaming ended unexpectedly");
            }
        }, ct);
    }

    private void StopTrafficStreaming()
    {
        _trafficCts?.Cancel();
        _trafficCts?.Dispose();
        _trafficCts = null;
    }

    // ── Connection Guard Handler ─────────────────────────────────────────

    private void OnConnectionStatusChanged(ConnectionStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = status;
            StatusText = status switch
            {
                ConnectionStatus.Connected => L("Connected"),
                ConnectionStatus.Connecting => L("Connecting"),
                ConnectionStatus.Reconnecting => L("Reconnecting"),
                ConnectionStatus.Disconnecting => L("Disconnecting"),
                ConnectionStatus.Disconnected => L("Disconnected"),
                ConnectionStatus.Error => L("ConnectionLost"),
                _ => status.ToString()
            };

            if (status == ConnectionStatus.Disconnected)
            {
                StopConnectionTimer();
                StopTrafficStreaming();
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

    // ── Clash API Readiness ─────────────────────────────────────────────

    /// <summary>
    /// Polls the Clash API health endpoint until it responds or the timeout expires.
    /// This ensures sing-box has fully initialized before starting traffic streaming
    /// and connection guard monitoring.
    /// </summary>
    private async Task<bool> WaitForClashApiAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await _clashApi.HealthCheckAsync())
            {
                Logger.Debug("Clash API ready after {Elapsed}ms", sw.ElapsedMilliseconds);
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Convert a byte count to a human-readable size string (e.g. "44.8 MB", "12.3 GB", "1.2 TB").
    /// </summary>
    private static string BytesToHumanSize(long bytes)
    {
        if (bytes <= 0) return "0 B";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:F1} {units[unitIndex]}";
    }

    /// <summary>
    /// Open the subscription renewal/support URL in the default browser.
    /// </summary>
    private void OpenRenewUrl()
    {
        var url = SubscriptionInfo?.SupportUrl;
        if (string.IsNullOrEmpty(url))
            url = SubscriptionInfo?.WebPageUrl;

        if (string.IsNullOrEmpty(url))
        {
            Logger.Debug("No support or web page URL available for renewal");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to open renewal URL: {Url}", url);
        }
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

    /// <summary>
    /// Convert byte count to a human-readable size string (no "/s").
    /// </summary>
    private static string BytesToHumanSize(long bytes)
    {
        if (bytes <= 0) return "0 B";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:F1} {units[unitIndex]}";
    }

    // ── Settings Changed ─────────────────────────────────────────────────

    private async void OnSettingsChanged()
    {
        var newUrl = _settingsService.Current.SubscriptionUrl;
        if (string.Equals(newUrl, _lastSubscriptionUrl, StringComparison.Ordinal))
            return;

        _lastSubscriptionUrl = newUrl;
        Logger.Information("Subscription URL changed, refreshing servers...");
        await RefreshServersAsync();
    }

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _settingsService.OnSettingsChanged -= OnSettingsChanged;
        _connectionGuard.OnStatusChanged -= OnConnectionStatusChanged;
        StopConnectionTimer();
        StopTrafficStreaming();
        _connectGate.Dispose();

        GC.SuppressFinalize(this);
    }
}
