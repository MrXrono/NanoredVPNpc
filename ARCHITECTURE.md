# NanoredVPN — Architecture & Code Map

> Version: 1.0.0 | Stack: C# / .NET 10 / Avalonia 11.3.12 / sing-box v1.12.22

---

## 1. Solution Structure

```
SingBoxClient.sln
├── src/SingBoxClient.Core/          # Business logic (class library, net10.0)
│   ├── Models/                      # Data models and enums
│   ├── Config/                      # sing-box config.json generator
│   ├── Services/                    # All core services (15 files)
│   ├── Platform/                    # OS-specific abstractions
│   ├── Helpers/                     # Utility classes
│   └── Constants/                   # App-wide constants
│
├── src/SingBoxClient.Desktop/       # Avalonia UI (WinExe, net10.0)
│   ├── Program.cs                   # Entry point
│   ├── App.axaml(.cs)               # DI container, theme, lifecycle
│   ├── ViewModels/                  # MVVM ViewModels (ReactiveUI)
│   ├── Views/                       # AXAML views (7 views)
│   ├── Controls/                    # Custom controls (3 controls)
│   ├── Converters/                  # Value converters
│   ├── Themes/                      # Dark/Light theme resources
│   ├── Localization/                # EN/RU string resources
│   ├── Assets/                      # Icons, images
│   └── Services/                    # UI-specific services (TrayIcon)
│
├── runtime/                         # sing-box binaries (per-platform)
│   ├── win-x64/sing-box.exe
│   └── win-arm64/sing-box.exe
│
└── build/                           # Publish scripts
    ├── publish-win-x64.sh
    └── publish-win-arm64.sh
```

---

## 2. NuGet Dependencies

### SingBoxClient.Core

| Package | Version |
|---------|---------|
| Serilog | 4.3.1 |
| Serilog.Sinks.File | 7.0.0 |
| Serilog.Sinks.Console | 6.1.1 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.3 |

### SingBoxClient.Desktop

| Package | Version |
|---------|---------|
| Avalonia | 11.3.12 |
| Avalonia.Desktop | 11.3.12 |
| Avalonia.Themes.Fluent | 11.3.12 |
| Avalonia.ReactiveUI | 11.3.9 |
| Avalonia.Controls.DataGrid | 11.3.12 |
| Avalonia.Fonts.Inter | 11.3.12 |
| MessageBox.Avalonia | 3.3.1.1 |
| Microsoft.Extensions.DependencyInjection | 10.0.3 |
| Serilog | 4.3.1 |
| Serilog.Sinks.File | 7.0.0 |
| Serilog.Sinks.Console | 6.1.1 |

---

## 3. Data Flow Diagram

```
                        ┌─────────────────────────────┐
                        │     Backend API Server       │
                        │  (api.example.com)           │
                        └──────────┬──────────────────┘
                                   │ HTTPS
                    ┌──────────────┼──────────────────────┐
                    │              │                       │
              ┌─────▼─────┐ ┌─────▼──────┐ ┌────────────▼─────┐
              │ ApiClient  │ │Subscription│ │RemoteConfig      │
              │            │ │Service     │ │Service            │
              │• Updates   │ │• Fetch sub │ │• Fetch rules      │
              │• Analytics │ │• Parse     │ │• Cache             │
              │• Crashes   │ │  servers   │ │                    │
              │• Announce  │ │• Cache     │ │                    │
              └─────┬──────┘ └─────┬──────┘ └────────┬──────────┘
                    │              │                   │
    ┌───────────────┼──────────────┼───────────────────┼──────────────┐
    │               │    CORE SERVICE LAYER            │              │
    │  ┌────────────▼──────────────▼───────────────────▼────────────┐ │
    │  │                    HomeViewModel                           │ │
    │  │  ConnectCommand flow:                                     │ │
    │  │  1. PingService.GetBestInCountry()                        │ │
    │  │  2. RoutingService.GetRules() + RemoteConfig.GetCached()  │ │
    │  │  3. ISingBoxConfigBuilder.BuildAndSave(server)            │ │
    │  │  4. SingBoxProcessManager.StartAsync()                    │ │
    │  │  5. WindowsPlatform.SetSystemProxy() [if Proxy mode]      │ │
    │  │  6. ConnectionGuard.StartMonitoring()                     │ │
    │  │  7. ClashApiClient → traffic polling (DispatcherTimer)    │ │
    │  └───────────────────────────┬────────────────────────────────┘ │
    │                              │                                  │
    │              ┌───────────────▼───────────────┐                  │
    │              │   SingBoxProcessManager       │                  │
    │              │   Process.Start("sing-box")   │                  │
    │              │   stdout → LogService          │                  │
    │              │   Exited → ConnectionGuard     │                  │
    │              └───────────────┬───────────────┘                  │
    └──────────────────────────────┼──────────────────────────────────┘
                                   │ Process
                    ┌──────────────▼──────────────┐
                    │       sing-box.exe           │
                    │  ┌────────────────────────┐  │
                    │  │ Inbound:               │  │
                    │  │  mixed (127.0.0.1:2080)│  │
                    │  │  tun (172.19.0.1/30)   │  │
                    │  ├────────────────────────┤  │
                    │  │ Outbound:              │  │
                    │  │  VLESS/VMess/Trojan/SS │  │
                    │  │  direct / block        │  │
                    │  ├────────────────────────┤  │
                    │  │ Route: user rules      │  │
                    │  │ DNS: DoH (Google/CF)   │  │
                    │  ├────────────────────────┤  │
                    │  │ Clash API :9090        │◄─┼── ClashApiClient (HTTP)
                    │  └────────────────────────┘  │
                    └──────────────────────────────┘
```

---

## 4. Models (`SingBoxClient.Core.Models`)

| File | Description | Key Fields |
|------|-------------|------------|
| `ConnectionStatus.cs` | Enum | Disconnected=0, Connecting=1, Connected=2, Reconnecting=3, Error=4, Disconnecting=5 |
| `ConnectionMode.cs` | Enum | Proxy, TUN |
| `ServerNode.cs` | Server from subscription | Protocol, Address, Port, UuidOrPassword, TlsSettings, Transport, Name, Latency, IsReachable, ShadowsocksMethod |
| `CountryGroup.cs` | Country grouping | Code, DisplayName, Servers[], BestServer, AverageLatency |
| `SubscriptionData.cs` | Subscription metadata | Id, ExpiresAt, TotalTraffic, UsedTraffic, UpdateInterval, ProfileTitle |
| `RoutingRule.cs` | Routing rule (INotifyPropertyChanged) | Id, Type(enum), Value, Action(enum), IsRemote, IsEnabled, Priority |
| `AppSettings.cs` | All app settings | ProxyEnabled, TunEnabled, ProxyPort, Theme, Language, AutoStart, AutoConnect, RemoteConfigEnabled, DebugMode, TunBypassApps, TunProxyApps, TunBlockApps, SubscriptionUrl, SelectedCountry |
| `PingResult.cs` | Ping result | Server, LatencyMs, IsReachable |
| `TrafficStats.cs` | Real-time traffic | UploadSpeed, DownloadSpeed (bytes/sec), TotalUpload, TotalDownload |
| `Announcement.cs` | Server notification | Id, Title, Body, CreatedAt, IsRead |
| `UpdateInfo.cs` | Update info | Available, Version, DownloadUrl, SingBoxUrl, ReleaseNotes |
| `AnalyticsEvent.cs` | Analytics event | EventName, Properties{}, Timestamp |
| `TlsSettings.cs` | TLS config | ServerName, Fingerprint, Alpn[], AllowInsecure, RealityPublicKey, RealityShortId, IsReality (computed) |
| `TransportSettings.cs` | Transport config | Type, Path, Host, ServiceName |

**Note:** `RoutingRule` implements `INotifyPropertyChanged` with backing fields and `PropertyChanged` event for all 7 properties. This enables DataGrid live editing in RoutingView.

---

## 5. Config Generator (`SingBoxClient.Core.Config`)

Generates `Configuration/config.json` for sing-box runtime.

| File | Builds Section | Key Logic |
|------|---------------|-----------|
| `SingBoxConfigBuilder.cs` | Full config | Static orchestrator — builds all sections, serializes to JSON |
| `InboundConfig.cs` | `inbounds[]` | Mixed proxy (port) and/or TUN (with per-app rules) |
| `OutboundConfig.cs` | `outbounds[]` | Server (VLESS/VMess/Trojan/SS) + direct + block + dns |
| `RouteConfig.cs` | `route` | Merges remote + user rules, auto_detect_interface, final=proxy |
| `DnsConfig.cs` | `dns` | DoH servers (Google, CF), FakeIP for TUN mode |
| `ExperimentalConfig.cs` | `experimental` | Clash API on :9090, cache_file |

**Config generation flow:**
```
ISingBoxConfigBuilder.BuildAndSave(server)
  └── SingBoxConfigBuilderService (reads ISettingsService + IRoutingService)
        └── SingBoxConfigBuilder.Build(mode, proxyEnabled, tunEnabled, port, server, rules, bypass, proxy, debug)
              ├── InboundConfig.BuildMixedProxy(port)      [if proxyEnabled]
              ├── InboundConfig.BuildTun(include, exclude)  [if tunEnabled]
              ├── OutboundConfig.BuildServerOutbound(server) + Direct + Block + Dns
              ├── RouteConfig.Build(mergedRules)
              ├── DnsConfig.Build(useFakeIp: tunEnabled)
              └── ExperimentalConfig.Build(9090)
              → JSON string → write to Configuration/config.json
```

---

## 6. Services (`SingBoxClient.Core.Services`)

### Process & Connection

| Service | Interface | Responsibility | Interacts With |
|---------|-----------|---------------|----------------|
| `SingBoxProcessManager` | `ISingBoxProcessManager` | Start/Stop/Restart sing-box.exe, capture stdout | IPlatformService |
| `ClashApiClient` | `IClashApiClient` | HTTP to localhost:9090 — health, traffic, proxies | sing-box Clash API |
| `ConnectionGuardService` | `IConnectionGuardService` | Health monitoring every 5s, auto-reconnect, failover | ClashApiClient, SingBoxProcessManager, ISingBoxConfigBuilder |
| `SingBoxConfigBuilderService` | `ISingBoxConfigBuilder` | Wraps static ConfigBuilder with DI services, writes config.json | ISettingsService, IRoutingService |

**ConnectionGuard state machine:**
```
Connecting → Connected → [health fail x3] → Reconnecting → Connected
                                                    ↓ all servers fail
                                               Disconnected (Error)
```

### Subscription & Servers

| Service | Interface | Responsibility | Interacts With |
|---------|-----------|---------------|----------------|
| `SubscriptionService` | `ISubscriptionService` | Fetch + parse subscription URL, cache servers | ShareLinkParser, HttpClient |
| `CountryGroupingService` | `ICountryGroupingService` | Group servers by country code prefix | CountryCodeHelper |
| `PingService` | `IPingService` | TCP ping servers (max 10 concurrent), rank by latency, GetBestServerAsync | ServerNode, CountryGroup |

### Configuration & Rules

| Service | Interface | Responsibility | Interacts With |
|---------|-----------|---------------|----------------|
| `RoutingService` | `IRoutingService` | CRUD routing rules (GetRules, GetAllRules, GetEnabledRules, SaveRules), load/save routing.json | Configuration/routing.json |
| `SettingsService` | `ISettingsService` | Load/save settings.json, Settings + Current properties | Configuration/settings.json |
| `RemoteConfigService` | `IRemoteConfigService` | FetchRoutingRulesAsync, FetchAnnouncementsAsync, cache | ApiClient |

### Backend Communication

| Service | Interface | Responsibility | Interacts With |
|---------|-----------|---------------|----------------|
| `ApiClient` | `IApiClient` | All backend API calls (updates, analytics, config) | HttpClient → backend |
| `UpdateService` | `IUpdateService` | CheckForUpdateAsync + download + apply updates | ApiClient |
| `AnalyticsService` | `IAnalyticsService` | Buffer events (TrackAsync), batch send, crash logs | ApiClient |
| `AnnouncementService` | `IAnnouncementService` | Fetch server notifications, GetAll() | ApiClient |
| `LogService` | `ILogService` | Log rotation (10MB x3), dedup via offset marker | Logs/ |

---

## 7. Platform Layer (`SingBoxClient.Core.Platform`)

| File | Description |
|------|-------------|
| `IPlatformService.cs` | Interface: SetSystemProxy, ClearSystemProxy, SetAutoStart, IsAdmin, etc. |
| `WindowsPlatformService.cs` | Windows impl: Registry for proxy/autostart, WindowsIdentity for admin check |
| `FirewallService.cs` | Windows Firewall: auto-add inbound/outbound rules for sing-box via netsh |

**Registry paths used:**
- System proxy: `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`
- Auto-start: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

---

## 8. Helpers (`SingBoxClient.Core.Helpers`)

| File | Key Methods |
|------|-------------|
| `ShareLinkParser.cs` | Parse(link) → ServerNode, supports vless://, vmess://, trojan://, ss:// |
| `Base64Helper.cs` | Decode (standard + URL-safe), IsBase64 |
| `CountryCodeHelper.cs` | ExtractCountryCode, GetFlag (emoji), GetDisplayName (22 countries EN/RU) |
| `HttpClientFactory.cs` | CreateDefault, CreateIgnoreCert, CreateApiClient |

---

## 9. UI Layer (`SingBoxClient.Desktop`)

### Dependency Injection (App.axaml.cs)

**Core Services (Singleton):**
- ISettingsService → SettingsService
- ISingBoxProcessManager → SingBoxProcessManager
- IClashApiClient → ClashApiClient
- ISubscriptionService → SubscriptionService
- ICountryGroupingService → CountryGroupingService
- IPingService → PingService
- IConnectionGuardService → ConnectionGuardService
- IRoutingService → RoutingService
- IApiClient → ApiClient
- IUpdateService → UpdateService
- IAnalyticsService → AnalyticsService
- ILogService → LogService
- IAnnouncementService → AnnouncementService
- IRemoteConfigService → RemoteConfigService
- ISingBoxConfigBuilder → SingBoxConfigBuilderService
- IPlatformService → WindowsPlatformService

**Desktop Services (Singleton):**
- TrayIconService

**ViewModels (Transient):**
- MainViewModel, HomeViewModel, RoutingViewModel, TunSettingsViewModel, LogsViewModel, SettingsViewModel, AnnouncementsViewModel

### ViewModels (MVVM, ReactiveUI)

| ViewModel | View | Key Properties | Key Commands |
|-----------|------|----------------|--------------|
| `MainViewModel` | `MainWindow` | CurrentPage, IsConnected, ConnectionStatus, IsDarkTheme, IsNotNavigating | NavigateCommand, ToggleThemeCommand, MinimizeCommand, MaximizeCommand, CloseCommand |
| `HomeViewModel` | `HomeView` | IsProxyEnabled, IsTunEnabled, Status, StatusText, ConnectButtonText, Countries, SelectedCountry, Timer, UploadSpeed, DownloadSpeed, SubscriptionId, ExpiresAt, TrafficUsageText, TrafficPercent, HasAnnouncements | ConnectCommand, DisconnectCommand, ToggleConnectionCommand, RefreshServersCommand, RenewCommand |
| `RoutingViewModel` | `RoutingView` | Rules (ObservableCollection), IsRemoteConfigEnabled, NewRuleValue, NewRuleAction, RuleActions | AddRuleCommand, RemoveRuleCommand, SaveCommand, SyncCommand, ToggleRuleCommand, MoveRuleUpCommand, MoveRuleDownCommand, DeleteRuleCommand |
| `TunSettingsViewModel` | `TunSettingsView` | BypassApps, ProxyApps, BlockApps | SaveCommand, CancelCommand |
| `LogsViewModel` | `LogsView` | LogText, AutoScroll | ClearCommand, CopyCommand |
| `SettingsViewModel` | `SettingsView` | ProxyPort (decimal), Language, MinimizeToTray, AutoStart, AutoConnect, DebugMode, SubscriptionUrl | SaveCommand, CancelCommand, CopySubscriptionUrlCommand |
| `AnnouncementsViewModel` | `AnnouncementsWindow` | Announcements, CloseAction | MarkAllReadCommand, CloseCommand |

### Views

| View | Layout |
|------|--------|
| `MainWindow.axaml` | Custom title bar (Minimize/Maximize/Close) + Sidebar (64px) + ContentControl (page switching) |
| `HomeView.axaml` | Mode checkboxes → Connect panel → Info panels (2-col) → Country list |
| `RoutingView.axaml` | Header + Remote config toggle → DataGrid (editable columns) → Add/Save buttons |
| `TunSettingsView.axaml` | Header → 3-column TextBoxes (bypass/proxy/block) |
| `LogsView.axaml` | Header → Monospace TextBlock with auto-scroll → Clear/Copy buttons |
| `SettingsView.axaml` | Sections: Port (NumericUpDown) → Language/Toggles → Subscription URL |
| `AnnouncementsWindow.axaml` | Modal window with announcements list + mark all read |

### Custom Controls

| Control | Description |
|---------|-------------|
| `CountrySelector` | ListBox with flag + name + ping (LetterSpacing for flag), bound to Countries/SelectedCountry |
| `TrafficWidget` | Upload/Download speed display with arrows |
| `StatusIndicator` | Colored dot (12px Ellipse) based on ConnectionStatus |

### Themes

| File | Description |
|------|-------------|
| `DarkTheme.axaml` | Dark: BgMain=#0F0F13, Accent=#6C5CE7, TextPrimary=#E8E8F0 |
| `LightTheme.axaml` | Light: BgMain=#F5F5F8, same Accent, TextPrimary=#1A1A2E |

Themes loaded via `ResourceDictionary.MergedDictionaries` in `App.axaml`.

### Localization

| File | Language | Keys |
|------|----------|------|
| `Strings.resx` | English (default) | Connect, Disconnect, Home, Routing, Settings, ... |
| `Strings.ru.resx` | Russian | Подключить, Отключить, Главная, Маршрутизация, ... |

---

## 10. Startup Flow (`Program.cs`)

```
0. SetupLibsResolver() — register AssemblyLoadContext fallback for Core/, libs/, dotnet/
   (must run BEFORE any third-party type is loaded; RunApplication is [NoInlining])
1. Mutex check (single instance)
2. Serilog init (file + console)
3. Global exception handlers
4. --cleanup-update flag handling (delete *.bak files after self-update)
5. Avalonia AppBuilder → App.Initialize()
6. DI container build (all services + ViewModels)
7. SettingsService.Load()
8. MainWindow + MainViewModel
9. On shutdown: Stop sing-box → Clear proxy → Save settings → Flush analytics → Dispose TrayIcon
```

---

## 11. File Storage

```
Configuration/                    ← Runtime config (created on first launch)
├── settings.json                 ← AppSettings (proxy, tun, language, theme, ...)
├── servers.json                  ← Cached server list from subscription
├── routing.json                  ← User routing rules
├── config.json                   ← Generated sing-box config (runtime)
└── update/                       ← Temporary update download directory

Logs/                             ← Application logs
├── app.log                       ← Application log (Serilog, 10MB rotation)
├── app.1.log                     ← Rotated log
├── singbox.log                   ← sing-box stdout capture
├── .sent_marker.json             ← Log dedup offset marker
└── crash_*.log                   ← Crash reports (sent + deleted)
```

Paths are defined in `AppDefaults.ConfigDir` ("Configuration") and `AppDefaults.LogsDir` ("Logs").

---

## 12. API Endpoints (Backend)

| Method | Endpoint | Used By |
|--------|----------|---------|
| POST | `/api/v1/analytics/crash` | AnalyticsService |
| POST | `/api/v1/analytics/event` | AnalyticsService |
| GET | `/api/v1/analytics/debug-request` | AnalyticsService |
| POST | `/api/v1/analytics/debug-logs` | AnalyticsService |
| GET | `/api/v1/update/check?v=&arch=` | UpdateService |
| GET | `/api/v1/update/download/{id}` | UpdateService |
| GET | `/api/v1/subscription/status` | ApiClient |
| GET | `/api/v1/config/remote` | RemoteConfigService |
| GET | `/api/v1/announcements?since=` | AnnouncementService |

---

## 13. sing-box Clash API (localhost:9090)

| Method | Path | Used By |
|--------|------|---------|
| GET | `/` | ClashApiClient.HealthCheckAsync |
| GET | `/traffic` | ClashApiClient.GetTrafficAsync |
| GET | `/proxies` | ClashApiClient.GetProxiesAsync |
| DELETE | `/connections` | ClashApiClient.CloseAllConnectionsAsync |

---

## 14. Routing Priority

```
1. TUN Per-App (exclude_process / include_process / Firewall block)
2. Remote config rules (if accepted)
3. Custom user rules (ordered by Priority)
4. Default: private IP → direct
5. Final: all traffic → proxy
```

---

## 15. Key Architectural Patterns

| Pattern | Implementation |
|---------|----------------|
| Dependency Injection | Microsoft.Extensions.DependencyInjection, constructor injection |
| MVVM | ReactiveUI ViewModels with data-bound Avalonia views |
| Reactive Commands | ReactiveCommand<TInput, TOutput> for all UI actions |
| Singleton Services | All core services share single instance across app |
| Transient ViewModels | Fresh instance per page navigation |
| INotifyPropertyChanged | RoutingRule model for DataGrid live editing |
| RaiseAndSetIfChanged | ReactiveObject pattern in all ViewModels |
| Event-Driven | OnStatusChanged (ConnectionGuard), OnLogLine (ProcessManager/LogService) |
| Config Builder | Static builder class wrapped in ISingBoxConfigBuilder service interface |
| Service Locator | App.Services static IServiceProvider for edge cases |
| UI Thread Marshaling | Avalonia Dispatcher.UIThread for background → UI updates |
| Cancellation Tokens | CancellationTokenSource for traffic polling, health checks |
| IDisposable | ViewModels unsubscribe from events in Dispose() |

---

## 16. Deployment Layout & Assembly Resolution

Published output is organized by the `build/publish-*.sh` scripts:

```
dist/win-x64/
├── SingBoxClient.Desktop.exe        # App entry point
├── SingBoxClient.Desktop.dll        # App assembly (must stay in root — loaded by apphost)
├── SingBoxClient.Desktop.deps.json  # Dependency manifest
├── SingBoxClient.Desktop.runtimeconfig.json
├── coreclr.dll / clrjit.dll         # Native runtime
├── hostfxr.dll / hostpolicy.dll     # .NET host
├── System.Private.CoreLib.dll       # Core type system (loaded by coreclr before managed code)
├── System.Runtime.dll               # Type forwarders (needed before managed resolver runs)
├── System.Runtime.Loader.dll        # AssemblyLoadContext (needed by SetupLibsResolver)
├── sing-box.exe                     # VPN core binary
├── createdump.exe                   # Crash dump utility
│
├── Core/                            # Application assemblies (1 file)
│   └── SingBoxClient.Core.dll       # Core business logic library
│
├── dotnet/                          # .NET framework assemblies (~178 files)
│   ├── System.*.dll                 # Runtime libraries
│   ├── Microsoft.*.dll              # Framework extensions
│   ├── netstandard.dll / mscorlib.dll / WindowsBase.dll
│   └── mscordaccore.dll / mscordbi.dll  # Diagnostics
│
├── libs/                            # Third-party libraries (~44 files)
│   ├── Avalonia*.dll
│   ├── ReactiveUI*.dll
│   ├── Serilog*.dll
│   ├── SkiaSharp*.dll
│   └── ...
│
├── Configuration/                   # Runtime config (created on first launch)
│   ├── settings.json
│   ├── servers.json
│   ├── routing.json
│   └── config.json
│
└── Logs/                            # Application logs (created on first launch)
    ├── app.log
    └── singbox.log
```

**Assembly resolution:** `Program.SetupLibsResolver()` registers an `AssemblyLoadContext.Default.Resolving` handler that probes three subdirectories: `Core/` (app assemblies), `libs/` (third-party), and `dotnet/` (.NET framework). All three directories are also prepended to the `PATH` environment variable for native P/Invoke resolution (SkiaSharp, HarfBuzzSharp, etc.). `System.Private.CoreLib.dll` must remain in the app root — it is loaded by `coreclr` before any managed code executes. `SingBoxClient.Desktop.dll` must also remain in the root — it is loaded by the apphost. All other assemblies are resolved through the handler. `RunApplication()` is decorated with `[MethodImpl(MethodImplOptions.NoInlining)]` to prevent JIT from loading Avalonia/Serilog before the resolver is registered.

---

## 17. Config Example (generated)

```json
{
  "log": { "level": "info", "timestamp": true },
  "dns": {
    "servers": [
      { "tag": "google-doh", "address": "https://dns.google/dns-query", "detour": "proxy" },
      { "tag": "direct-dns", "address": "223.5.5.5", "detour": "direct" }
    ],
    "rules": [{ "domain_suffix": [".local",".localhost"], "server": "direct-dns" }]
  },
  "inbounds": [
    { "type": "mixed", "tag": "mixed-in", "listen": "127.0.0.1", "listen_port": 2080 },
    { "type": "tun", "tag": "tun-in", "auto_route": true, "strict_route": true, "inet4_address": "172.19.0.1/30" }
  ],
  "outbounds": [
    { "type": "vless", "tag": "proxy", "server": "de-vless-1.example.com", "server_port": 443, "uuid": "..." },
    { "type": "direct", "tag": "direct" },
    { "type": "block", "tag": "block" },
    { "type": "dns", "tag": "dns-out" }
  ],
  "route": {
    "rules": [
      { "protocol": "dns", "outbound": "dns-out" },
      { "ip_is_private": true, "outbound": "direct" },
      { "domain_suffix": [".youtube.com",".google.com"], "outbound": "proxy" }
    ],
    "auto_detect_interface": true,
    "final": "proxy"
  },
  "experimental": {
    "clash_api": { "external_controller": "127.0.0.1:9090" },
    "cache_file": { "enabled": true, "path": "cache.db" }
  }
}
```
