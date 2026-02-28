using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.ReactiveUI;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace SingBoxClient.Desktop;

class Program
{
    /// <summary>
    /// Serilog level switch — allows changing the minimum log level at runtime
    /// when DebugMode is toggled in settings.
    /// </summary>
    public static readonly LoggingLevelSwitch LogLevelSwitch = new(LogEventLevel.Debug);

    [STAThread]
    public static void Main(string[] args)
    {
        // Setup assembly resolver for libs/ and dotnet/ subdirectories BEFORE any third-party types are loaded.
        // This must run before Avalonia, Serilog, ReactiveUI, System.Threading, etc. are referenced.
        SetupLibsResolver();

        // All third-party type usage is deferred to RunApplication.
        // NoInlining ensures JIT won't compile it (and try to load Avalonia/Serilog)
        // until the resolver above is registered.
        RunApplication(args);
    }

    private static void SetupLibsResolver()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var probeDirs = new[]
        {
            Path.Combine(baseDir, "Core"),   // App assemblies: SingBoxClient.Core
            Path.Combine(baseDir, "libs"),   // Third-party: Avalonia, ReactiveUI, Serilog, SkiaSharp
            Path.Combine(baseDir, "dotnet")  // .NET framework: System.*, Microsoft.*, netstandard
        };

        // Managed assembly resolver — fallback when TPA doesn't find the DLL in app root
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            foreach (var dir in probeDirs)
            {
                var dllPath = Path.Combine(dir, $"{assemblyName.Name}.dll");
                if (File.Exists(dllPath))
                    return context.LoadFromAssemblyPath(dllPath);
            }
            return null;
        };

        // Native library resolver — add probe dirs to DLL search path for P/Invoke
        // (SkiaSharp, HarfBuzzSharp, Avalonia native, etc.)
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extraPaths = string.Join(Path.PathSeparator, probeDirs);
        Environment.SetEnvironmentVariable("PATH", extraPaths + Path.PathSeparator + currentPath);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApplication(string[] args)
    {
        // === Everything below uses third-party types (Serilog, Avalonia, ReactiveUI) ===

        // 1. Single Instance check
        const string mutexName = "NanoredVPN_SingleInstance";
        using var mutex = new System.Threading.Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
            return;

        // 2. Setup Serilog (absolute path — UAC changes working dir to System32)
        var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "app.log");
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.ControlledBy(LogLevelSwitch)
            .WriteTo.File(logPath,
                rollingInterval: Serilog.RollingInterval.Infinite,
                fileSizeLimitBytes: 10_485_760,
                retainedFileCountLimit: 3,
                rollOnFileSizeLimit: true)
            .WriteTo.Console()
            .CreateLogger();

        // 3. Global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Serilog.Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
            Serilog.Log.CloseAndFlush();
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Serilog.Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        // 4. Handle --cleanup-update flag
        if (args.Length > 0 && args[0] == "--cleanup-update")
        {
            System.Threading.Thread.Sleep(2000);
            CleanupUpdateFiles();
        }

        try
        {
            Serilog.Log.Information("NanoredVPN v1.0.0 starting");
            Avalonia.AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .UseReactiveUI()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }

    private static void CleanupUpdateFiles()
    {
        try
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var bak in Directory.GetFiles(dir, "*.bak"))
            {
                File.Delete(bak);
                Serilog.Log.Information("Cleaned up update file: {File}", Path.GetFileName(bak));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to cleanup update files");
        }
    }
}
