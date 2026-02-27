using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Threading;
using System.Runtime.InteropServices;
using Serilog;

namespace SingBoxClient.Desktop;

class Program
{
    private static Mutex? _mutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 1. Single Instance check
        const string mutexName = "NanoredVPN_SingleInstance";
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            // Already running
            return;
        }

        // 2. Setup Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File("data/logs/app.log",
                rollingInterval: RollingInterval.Infinite,
                fileSizeLimitBytes: 10_485_760,
                retainedFileCountLimit: 3,
                rollOnFileSizeLimit: true)
            .WriteTo.Console()
            .CreateLogger();

        // 3. Global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        // 4. Handle --cleanup-update flag
        if (args.Length > 0 && args[0] == "--cleanup-update")
        {
            Thread.Sleep(2000);
            CleanupUpdateFiles();
        }

        try
        {
            Log.Information("NanoredVPN v1.0.0 starting");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();

    private static void CleanupUpdateFiles()
    {
        try
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var bak in Directory.GetFiles(dir, "*.bak"))
            {
                File.Delete(bak);
                Log.Information("Cleaned up update file: {File}", Path.GetFileName(bak));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to cleanup update files");
        }
    }
}
