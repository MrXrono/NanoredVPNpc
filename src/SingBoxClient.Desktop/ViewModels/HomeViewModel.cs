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
            this.RaiseAndSetIfChanged(ref _isTunEnabled, value);
            if (value) IsProxyEnabled = false;
        }
    }

    private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;
    public ConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

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
        set => this.RaiseAndSetIfChanged(ref _subscriptionInfo, value);
    }

    private ObservableCollection<Announcement> _announcements = new();
    public ObservableCollection<Announcement> Announcements
    {
        get => _announcements;
        set => this.RaiseAndSetIfChanged(ref _announcements, value);
    }

    private bool _isConnecting;
    public bool IsConnecting
    {
        get => _isConnecting;
        set => this.RaiseAndSetIfChanged(ref _isConnecting, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshServersCommand { get; }

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
        IAnnouncementService announcementService)
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

            // 2. Get merged routing rules (local + remote)
            var localRules = _routingService.GetEnabledRules();
            List<RoutingRule> remoteRules;
            try
            {
                remoteRules = _settingsService.Current.RemoteConfigEnabled
                    ? await _remoteConfigService.FetchRoutingRulesAsync()
                    : new List<RoutingRule>();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to fetch remote config, using local rules only");
                remoteRules = new List<RoutingRule>();
            }

            var mergedRules = localRules.Concat(remoteRules).ToList();

            // 3. Build sing-box configuration
            var configBuilder = new SingBoxConfigBuilder(_settingsService);
            var configPath = configBuilder.BuildAndSave(bestServer, mergedRules, IsProxyEnabled, IsTunEnabled);

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

                group.AverageLatency = group.Servers
                    .Where(s => s.IsReachable && s.Latency >= 0)
                    .Select(s => s.Latency)
                    .DefaultIfEmpty(0)
                    .Average()
                    .GetHashCode(); // int conversion
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
                ConnectionStatus.Reconnecting => "Reconnecting...",
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

// ── Forward-declared service interfaces ──────────────────────────────────────
// These are referenced by the DI container and will be implemented in separate files.

namespace SingBoxClient.Core.Services
{
    /// <summary>
    /// Persists and loads application settings.
    /// </summary>
    public interface ISettingsService
    {
        AppSettings Current { get; }
        void Load();
        void Save();
    }

    /// <summary>
    /// Groups a flat list of servers by country code.
    /// </summary>
    public interface ICountryGroupingService
    {
        List<CountryGroup> GroupByCountry(List<ServerNode> servers);
    }

    /// <summary>
    /// Measures latency to servers via TCP/ICMP ping.
    /// </summary>
    public interface IPingService
    {
        Task PingAllAsync(List<ServerNode> servers);
        Task<ServerNode?> GetBestServerAsync(CountryGroup country);
    }

    /// <summary>
    /// Manages local routing rules (load/save from JSON file).
    /// </summary>
    public interface IRoutingService
    {
        List<RoutingRule> GetAllRules();
        List<RoutingRule> GetEnabledRules();
        void SaveRules(List<RoutingRule> rules);
    }

    /// <summary>
    /// Fetches remote configuration (routing rules, announcements) from the server API.
    /// </summary>
    public interface IRemoteConfigService
    {
        Task<List<RoutingRule>> FetchRoutingRulesAsync();
        Task<List<Announcement>> FetchAnnouncementsAsync();
    }

    /// <summary>
    /// Checks for application and sing-box core updates.
    /// </summary>
    public interface IUpdateService
    {
        Task<UpdateInfo?> CheckForUpdateAsync();
    }

    /// <summary>
    /// Collects and sends analytics/telemetry events.
    /// </summary>
    public interface IAnalyticsService
    {
        Task TrackAsync(AnalyticsEvent evt);
        Task FlushAsync();
    }

    /// <summary>
    /// Application-level structured log service.
    /// </summary>
    public interface ILogService
    {
        event Action<string>? OnNewLogLine;
    }

    /// <summary>
    /// Manages server-side announcements / notifications.
    /// </summary>
    public interface IAnnouncementService
    {
        List<Announcement> GetAll();
        void MarkAllRead();
    }

    /// <summary>
    /// HTTP client for the provider's REST API (subscriptions, updates, analytics, etc.).
    /// </summary>
    public interface IApiClient
    {
        Task<HttpResponseMessage> GetAsync(string path);
        Task<HttpResponseMessage> PostAsync(string path, HttpContent content);
    }

    /// <summary>
    /// Builds a complete sing-box JSON configuration for a given server and rule set.
    /// </summary>
    public class SingBoxConfigBuilder
    {
        private readonly ISettingsService _settings;

        public SingBoxConfigBuilder(ISettingsService settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Build config for a server and write to data/config.json.
        /// Returns the absolute path to the generated file.
        /// </summary>
        public string BuildAndSave(ServerNode server, List<RoutingRule> rules, bool proxyMode, bool tunMode)
        {
            // Delegate to the existing ISingBoxConfigBuilder for the core config
            // and layer routing rules, proxy/tun inbound settings on top.
            var dataDir = AppDefaults.DataDir;
            Directory.CreateDirectory(dataDir);
            var configPath = Path.Combine(dataDir, AppDefaults.ConfigFileName);

            // Build the JSON config (simplified — full implementation in Config/ namespace)
            var config = BuildConfigJson(server, rules, proxyMode, tunMode);
            File.WriteAllText(configPath, config);

            return Path.GetFullPath(configPath);
        }

        private string BuildConfigJson(ServerNode server, List<RoutingRule> rules, bool proxyMode, bool tunMode)
        {
            // This is a simplified placeholder that produces valid sing-box JSON.
            // The actual implementation uses Config/InboundConfig, OutboundConfig, RouteConfig, DnsConfig.
            var port = _settings.Current.ProxyPort;

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                log = new { level = _settings.Current.DebugMode ? "debug" : "info" },
                inbounds = proxyMode
                    ? new object[]
                    {
                        new { type = "mixed", tag = "mixed-in", listen = "127.0.0.1", listen_port = port }
                    }
                    : new object[]
                    {
                        new { type = "tun", tag = "tun-in", address = new[] { AppDefaults.TunAddress }, auto_route = true, strict_route = true }
                    },
                outbounds = new object[]
                {
                    new
                    {
                        type = server.Protocol.ToString().ToLowerInvariant(),
                        tag = "proxy",
                        server = server.Address,
                        server_port = server.Port,
                        uuid = server.UuidOrPassword
                    },
                    new { type = "direct", tag = "direct" },
                    new { type = "block", tag = "block" }
                },
                experimental = new
                {
                    clash_api = new
                    {
                        external_controller = $"127.0.0.1:{AppDefaults.ClashApiPort}"
                    }
                }
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Settings service implementation that persists AppSettings to JSON on disk.
    /// </summary>
    public class SettingsService : ISettingsService
    {
        private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<SettingsService>();

        public AppSettings Current { get; private set; } = new();

        public void Load()
        {
            try
            {
                var path = Path.Combine(AppDefaults.DataDir, AppDefaults.SettingsFileName);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded is not null)
                        Current = loaded;
                }

                Logger.Debug("Settings loaded");
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to load settings, using defaults");
            }
        }

        public void Save()
        {
            try
            {
                var dir = AppDefaults.DataDir;
                Directory.CreateDirectory(dir);

                var path = Path.Combine(dir, AppDefaults.SettingsFileName);
                var json = System.Text.Json.JsonSerializer.Serialize(Current,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);

                Logger.Debug("Settings saved to {Path}", path);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save settings");
            }
        }
    }

    // Stub implementations for services that require API integration.
    // These will be fully implemented when the backend API is available.

    public class CountryGroupingService : ICountryGroupingService
    {
        public List<CountryGroup> GroupByCountry(List<ServerNode> servers)
        {
            return servers
                .GroupBy(s =>
                {
                    // Extract country code from server name (e.g. "DE-Frankfurt-01" → "DE")
                    var dash = s.Name.IndexOf('-');
                    return dash > 0 ? s.Name[..dash].ToUpperInvariant() : "XX";
                })
                .Select(g => new CountryGroup
                {
                    Code = g.Key,
                    DisplayName = Core.Helpers.CountryCodeHelper.GetDisplayName(g.Key),
                    Servers = g.ToList()
                })
                .ToList();
        }
    }

    public class PingService : IPingService
    {
        private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<PingService>();

        public async Task PingAllAsync(List<ServerNode> servers)
        {
            var semaphore = new SemaphoreSlim(AppDefaults.MaxConcurrentPings);
            var tasks = servers.Select(async server =>
            {
                await semaphore.WaitAsync();
                try
                {
                    server.Latency = await MeasureLatencyAsync(server);
                    server.IsReachable = server.Latency >= 0;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        public async Task<ServerNode?> GetBestServerAsync(CountryGroup country)
        {
            await PingAllAsync(country.Servers);
            return country.Servers
                .Where(s => s.IsReachable)
                .OrderBy(s => s.Latency)
                .FirstOrDefault();
        }

        private static async Task<int> MeasureLatencyAsync(ServerNode server)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var sw = Stopwatch.StartNew();
                var connectTask = tcp.ConnectAsync(server.Address, server.Port);
                var completed = await Task.WhenAny(connectTask,
                    Task.Delay(AppDefaults.PingTimeoutMs));

                if (completed == connectTask && tcp.Connected)
                {
                    sw.Stop();
                    return (int)sw.ElapsedMilliseconds;
                }

                return -1;
            }
            catch
            {
                return -1;
            }
        }
    }

    public class RoutingService : IRoutingService
    {
        private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<RoutingService>();
        private List<RoutingRule> _rules = new();

        public RoutingService()
        {
            LoadFromDisk();
        }

        public List<RoutingRule> GetAllRules() => new(_rules);

        public List<RoutingRule> GetEnabledRules() =>
            _rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList();

        public void SaveRules(List<RoutingRule> rules)
        {
            _rules = new List<RoutingRule>(rules);
            SaveToDisk();
        }

        private void LoadFromDisk()
        {
            try
            {
                var path = Path.Combine(AppDefaults.DataDir, AppDefaults.RoutingFileName);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var loaded = System.Text.Json.JsonSerializer.Deserialize<List<RoutingRule>>(json);
                    if (loaded is not null)
                        _rules = loaded;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to load routing rules");
            }
        }

        private void SaveToDisk()
        {
            try
            {
                var dir = AppDefaults.DataDir;
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, AppDefaults.RoutingFileName);
                var json = System.Text.Json.JsonSerializer.Serialize(_rules,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save routing rules");
            }
        }
    }

    public class RemoteConfigService : IRemoteConfigService
    {
        private readonly IApiClient _api;

        public RemoteConfigService(IApiClient api)
        {
            _api = api;
        }

        public async Task<List<RoutingRule>> FetchRoutingRulesAsync()
        {
            try
            {
                var response = await _api.GetAsync(ApiEndpoints.RemoteConfig);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                return System.Text.Json.JsonSerializer.Deserialize<List<RoutingRule>>(json) ?? new();
            }
            catch
            {
                return new List<RoutingRule>();
            }
        }

        public async Task<List<Announcement>> FetchAnnouncementsAsync()
        {
            try
            {
                var response = await _api.GetAsync(ApiEndpoints.Announcements);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                return System.Text.Json.JsonSerializer.Deserialize<List<Announcement>>(json) ?? new();
            }
            catch
            {
                return new List<Announcement>();
            }
        }
    }

    public class UpdateService : IUpdateService
    {
        private readonly IApiClient _api;

        public UpdateService(IApiClient api)
        {
            _api = api;
        }

        public async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                var response = await _api.GetAsync(ApiEndpoints.UpdateCheck);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                return System.Text.Json.JsonSerializer.Deserialize<UpdateInfo>(json);
            }
            catch
            {
                return null;
            }
        }
    }

    public class AnalyticsService : IAnalyticsService
    {
        private readonly List<AnalyticsEvent> _buffer = new();
        private readonly IApiClient _api;

        public AnalyticsService(IApiClient api)
        {
            _api = api;
        }

        public Task TrackAsync(AnalyticsEvent evt)
        {
            lock (_buffer)
            {
                _buffer.Add(evt);
            }

            if (_buffer.Count >= AppDefaults.AnalyticsBatchSize)
                return FlushAsync();

            return Task.CompletedTask;
        }

        public async Task FlushAsync()
        {
            List<AnalyticsEvent> batch;
            lock (_buffer)
            {
                batch = new List<AnalyticsEvent>(_buffer);
                _buffer.Clear();
            }

            if (batch.Count == 0) return;

            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(batch);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                await _api.PostAsync(ApiEndpoints.AnalyticsEvent, content);
            }
            catch
            {
                // Re-add to buffer on failure
                lock (_buffer)
                {
                    _buffer.InsertRange(0, batch);
                }
            }
        }
    }

    public class LogService : ILogService
    {
        public event Action<string>? OnNewLogLine;

        public void EmitLine(string line)
        {
            OnNewLogLine?.Invoke(line);
        }
    }

    public class AnnouncementService : IAnnouncementService
    {
        private readonly IRemoteConfigService _remoteConfig;
        private List<Announcement> _announcements = new();

        public AnnouncementService(IRemoteConfigService remoteConfig)
        {
            _remoteConfig = remoteConfig;
        }

        public List<Announcement> GetAll() => new(_announcements);

        public void MarkAllRead()
        {
            foreach (var a in _announcements)
                a.IsRead = true;
        }
    }

    public class ApiClient : IApiClient
    {
        private readonly HttpClient _http;

        public ApiClient()
        {
            _http = Core.Helpers.HttpClientFactory.CreateIgnoreCert();
            _http.BaseAddress = new Uri(ApiEndpoints.BaseUrl);
            _http.Timeout = TimeSpan.FromSeconds(15);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppDefaults.UserAgent);
        }

        public Task<HttpResponseMessage> GetAsync(string path) => _http.GetAsync(path);
        public Task<HttpResponseMessage> PostAsync(string path, HttpContent content) => _http.PostAsync(path, content);
    }
}
