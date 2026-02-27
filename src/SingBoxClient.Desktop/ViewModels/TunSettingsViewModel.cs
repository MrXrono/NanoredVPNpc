using System;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using Serilog;
using SingBoxClient.Core.Services;

namespace SingBoxClient.Desktop.ViewModels;

/// <summary>
/// ViewModel for TUN mode per-app routing settings (bypass / proxy / block lists).
/// </summary>
public class TunSettingsViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<TunSettingsViewModel>();

    private readonly ISettingsService _settingsService;

    // ── Properties ────────────────────────────────────────────────────────

    private string _bypassApps = string.Empty;
    public string BypassApps
    {
        get => _bypassApps;
        set => this.RaiseAndSetIfChanged(ref _bypassApps, value);
    }

    private string _proxyApps = string.Empty;
    public string ProxyApps
    {
        get => _proxyApps;
        set => this.RaiseAndSetIfChanged(ref _proxyApps, value);
    }

    private string _blockApps = string.Empty;
    public string BlockApps
    {
        get => _blockApps;
        set => this.RaiseAndSetIfChanged(ref _blockApps, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public TunSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        SaveCommand = ReactiveCommand.Create(Save);
        CancelCommand = ReactiveCommand.Create(LoadFromSettings);

        LoadFromSettings();
    }

    // ── Private ──────────────────────────────────────────────────────────

    private void LoadFromSettings()
    {
        try
        {
            var settings = _settingsService.Current;
            BypassApps = string.Join("\n", settings.TunBypassApps);
            ProxyApps = string.Join("\n", settings.TunProxyApps);
            BlockApps = string.Join("\n", settings.TunBlockApps);

            Logger.Debug("TUN settings loaded: bypass={Bypass}, proxy={Proxy}, block={Block}",
                settings.TunBypassApps.Count, settings.TunProxyApps.Count, settings.TunBlockApps.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load TUN settings");
        }
    }

    private void Save()
    {
        try
        {
            var settings = _settingsService.Current;

            settings.TunBypassApps = ParseAppList(BypassApps);
            settings.TunProxyApps = ParseAppList(ProxyApps);
            settings.TunBlockApps = ParseAppList(BlockApps);

            _settingsService.Save();

            Logger.Information("TUN settings saved: bypass={Bypass}, proxy={Proxy}, block={Block}",
                settings.TunBypassApps.Count, settings.TunProxyApps.Count, settings.TunBlockApps.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save TUN settings");
        }
    }

    private static List<string> ParseAppList(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new List<string>();

        return input
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .Distinct()
            .ToList();
    }
}
