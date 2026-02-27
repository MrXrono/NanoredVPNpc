using System;
using System.Collections.Generic;
using System.Reactive;
using ReactiveUI;
using Serilog;
using SingBoxClient.Core.Platform;
using SingBoxClient.Core.Services;

namespace SingBoxClient.Desktop.ViewModels;

/// <summary>
/// ViewModel for the application settings page — proxy port, language, autostart, etc.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<SettingsViewModel>();

    private readonly ISettingsService _settingsService;
    private readonly IPlatformService _platformService;

    // ── Properties ────────────────────────────────────────────────────────

    private int _proxyPort;
    public int ProxyPort
    {
        get => _proxyPort;
        set => this.RaiseAndSetIfChanged(ref _proxyPort, value);
    }

    private string _language = "English";
    public string Language
    {
        get => _language;
        set => this.RaiseAndSetIfChanged(ref _language, value);
    }

    private bool _minimizeToTray;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => this.RaiseAndSetIfChanged(ref _minimizeToTray, value);
    }

    private bool _autoStart;
    public bool AutoStart
    {
        get => _autoStart;
        set => this.RaiseAndSetIfChanged(ref _autoStart, value);
    }

    private bool _autoConnect;
    public bool AutoConnect
    {
        get => _autoConnect;
        set => this.RaiseAndSetIfChanged(ref _autoConnect, value);
    }

    private bool _debugMode;
    public bool DebugMode
    {
        get => _debugMode;
        set => this.RaiseAndSetIfChanged(ref _debugMode, value);
    }

    private string _subscriptionUrl = string.Empty;
    public string SubscriptionUrl
    {
        get => _subscriptionUrl;
        set => this.RaiseAndSetIfChanged(ref _subscriptionUrl, value);
    }

    public List<string> Languages { get; } = new() { "English", "\u0420\u0443\u0441\u0441\u043a\u0438\u0439" };

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public SettingsViewModel(
        ISettingsService settingsService,
        IPlatformService platformService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _platformService = platformService ?? throw new ArgumentNullException(nameof(platformService));

        SaveCommand = ReactiveCommand.Create(Save);
        CancelCommand = ReactiveCommand.Create(LoadFromSettings);

        LoadFromSettings();
    }

    // ── Private ──────────────────────────────────────────────────────────

    private void LoadFromSettings()
    {
        try
        {
            var s = _settingsService.Current;

            ProxyPort = s.ProxyPort;
            Language = MapLanguageCodeToDisplay(s.Language);
            MinimizeToTray = s.MinimizeToTray;
            AutoStart = s.AutoStart;
            AutoConnect = s.AutoConnect;
            DebugMode = s.DebugMode;
            SubscriptionUrl = s.SubscriptionUrl;

            Logger.Debug("Settings loaded into ViewModel");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load settings");
        }
    }

    private void Save()
    {
        try
        {
            // Validate proxy port
            if (ProxyPort < 1024 || ProxyPort > 65535)
            {
                Logger.Warning("Invalid proxy port {Port}, must be 1024-65535", ProxyPort);
                return;
            }

            var s = _settingsService.Current;

            s.ProxyPort = ProxyPort;
            s.Language = MapDisplayToLanguageCode(Language);
            s.MinimizeToTray = MinimizeToTray;
            s.AutoStart = AutoStart;
            s.AutoConnect = AutoConnect;
            s.DebugMode = DebugMode;
            s.SubscriptionUrl = SubscriptionUrl;

            _settingsService.Save();

            // Apply autostart to the platform (registry / startup folder)
            try
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                _platformService.SetAutoStart(AutoStart, exePath);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to apply autostart setting");
            }

            Logger.Information("Settings saved: port={Port}, lang={Lang}, autoStart={Auto}",
                ProxyPort, s.Language, AutoStart);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save settings");
        }
    }

    // ── Language Mapping ─────────────────────────────────────────────────

    private static string MapLanguageCodeToDisplay(string code)
    {
        return code switch
        {
            "ru" => "\u0420\u0443\u0441\u0441\u043a\u0438\u0439",
            "en" => "English",
            _ => "English"
        };
    }

    private static string MapDisplayToLanguageCode(string display)
    {
        return display switch
        {
            "\u0420\u0443\u0441\u0441\u043a\u0438\u0439" => "ru",
            "English" => "en",
            _ => "en"
        };
    }
}
