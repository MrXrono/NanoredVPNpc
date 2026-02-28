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
using System;
using System.Globalization;
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
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Switch theme at runtime by swapping the ResourceDictionary and Avalonia theme variant.
    /// </summary>
    public void ApplyTheme(bool isDark)
    {
        var themeUri = isDark
            ? new Uri("avares://SingBoxClient.Desktop/Themes/DarkTheme.axaml")
            : new Uri("avares://SingBoxClient.Desktop/Themes/LightTheme.axaml");

        var merged = Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(new ResourceInclude(themeUri) { Source = themeUri });

        RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    /// <summary>
    /// Switch UI language at runtime.
    /// </summary>
    public void ApplyLanguage(string langCode)
    {
        var culture = langCode switch
        {
            "ru" => new CultureInfo("ru-RU"),
            _ => new CultureInfo("en-US")
        };

        CultureInfo.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        LocalizationManager.Instance.SetCulture(culture);
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

            var trayService = Services.GetService<TrayIconService>();
            trayService?.Dispose();
        }
    }
}
