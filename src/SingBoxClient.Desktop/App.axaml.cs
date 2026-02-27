using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SingBoxClient.Core.Services;
using SingBoxClient.Core.Platform;
using SingBoxClient.Desktop.ViewModels;
using SingBoxClient.Desktop.Views;
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

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<RoutingViewModel>();
        services.AddTransient<TunSettingsViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<SettingsViewModel>();
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
        }
    }
}
