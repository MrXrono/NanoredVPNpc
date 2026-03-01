using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using SingBoxClient.Core.Services;
using SingBoxClient.Core.Platform;
using SingBoxClient.Desktop.Services;
using SingBoxClient.Desktop.ViewModels;
using SingBoxClient.Desktop.Views;
using Serilog.Events;
using System;
using System.Runtime.InteropServices;

namespace SingBoxClient.Desktop;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Load settings
        var settingsService = Services.GetRequiredService<ISettingsService>();
        settingsService.Load();

        // Apply DebugMode to Serilog level switch (and re-apply on every settings save)
        ApplyDebugMode(settingsService.Current.DebugMode);
        settingsService.OnSettingsChanged += () => ApplyDebugMode(settingsService.Current.DebugMode);

        // Apply saved theme
        ApplyTheme(settingsService.Current.Theme == "dark");

        // Apply saved language
        ApplyLanguage(settingsService.Current.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };

            desktop.ShutdownRequested += OnShutdownRequested;

            // Start UI thread watchdog — detects hangs and writes FATAL log from background thread
            Services.GetRequiredService<UiWatchdogService>().Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // MergedDictionaries layout: [0] = Theme, [1] = Language

    /// <summary>
    /// Switch theme at runtime by replacing slot 0 in MergedDictionaries.
    /// </summary>
    public void ApplyTheme(bool isDark)
    {
        var themeUri = isDark
            ? new Uri("avares://SingBoxClient.Desktop/Themes/DarkTheme.axaml")
            : new Uri("avares://SingBoxClient.Desktop/Themes/LightTheme.axaml");

        var merged = Resources.MergedDictionaries;
        var themeDict = new ResourceInclude(themeUri) { Source = themeUri };
        if (merged.Count > 0)
            merged[0] = themeDict;
        else
            merged.Add(themeDict);

        RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    /// <summary>
    /// Switch UI language at runtime by replacing slot 1 in MergedDictionaries.
    /// </summary>
    public void ApplyLanguage(string langCode)
    {
        var langUri = langCode == "ru"
            ? new Uri("avares://SingBoxClient.Desktop/Localization/RU.axaml")
            : new Uri("avares://SingBoxClient.Desktop/Localization/EN.axaml");

        var merged = Resources.MergedDictionaries;
        var langDict = new ResourceInclude(langUri) { Source = langUri };
        if (merged.Count > 1)
            merged[1] = langDict;
        else
            merged.Add(langDict);
    }

    /// <summary>
    /// Sync Serilog minimum level with the DebugMode setting.
    /// Debug=true → Verbose/Debug logs visible; Debug=false → Information and above only.
    /// </summary>
    private static void ApplyDebugMode(bool debugMode)
    {
        Program.LogLevelSwitch.MinimumLevel = debugMode
            ? LogEventLevel.Debug
            : LogEventLevel.Information;
    }

    private void ConfigureServices(ServiceCollection services)
    {
        // Platform
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<IPlatformService, WindowsPlatformService>();
        }

        // Core Services
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISingBoxProcessManager, SingBoxProcessManager>();
        services.AddSingleton<IClashApiClient, ClashApiClient>();
        services.AddSingleton<ISubscriptionService, SubscriptionService>();
        services.AddSingleton<ICountryGroupingService, CountryGroupingService>();
        services.AddSingleton<IPingService, PingService>();
        services.AddSingleton<IConnectionGuardService, ConnectionGuardService>();
        services.AddSingleton<IRoutingService, RoutingService>();
        services.AddSingleton<IApiClient, ApiClient>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IAnalyticsService, AnalyticsService>();
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IAnnouncementService, AnnouncementService>();
        services.AddSingleton<IRemoteConfigService, RemoteConfigService>();
        services.AddSingleton<ISingBoxConfigBuilder, SingBoxConfigBuilderService>();

        // Desktop Services
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<UiWatchdogService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<RoutingViewModel>();
        services.AddTransient<TunSettingsViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AnnouncementsViewModel>();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (Services != null)
        {
            var processManager = Services.GetService<ISingBoxProcessManager>();
            processManager?.StopAsync().GetAwaiter().GetResult();

            var platform = Services.GetService<IPlatformService>();
            platform?.ClearSystemProxy();

            var settings = Services.GetService<ISettingsService>();
            settings?.Save();

            var analytics = Services.GetService<IAnalyticsService>();
            analytics?.FlushAsync().GetAwaiter().GetResult();

            var watchdog = Services.GetService<UiWatchdogService>();
            watchdog?.Dispose();

            var trayService = Services.GetService<TrayIconService>();
            trayService?.Dispose();
        }
    }
}
