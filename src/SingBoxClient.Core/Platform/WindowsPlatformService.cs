using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Serilog;

namespace SingBoxClient.Core.Platform;

[SupportedOSPlatform("windows")]
public class WindowsPlatformService : IPlatformService
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const string RunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string AppName = "NanoredVPN";

    private static readonly ILogger Logger = Log.ForContext<WindowsPlatformService>();

    public void SetSystemProxy(string host, int port)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null)
            {
                Logger.Error("Failed to open Internet Settings registry key");
                return;
            }

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);

            Logger.Information("System proxy set to {Host}:{Port}", host, port);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to set system proxy to {Host}:{Port}", host, port);
        }
    }

    public void ClearSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null)
            {
                Logger.Error("Failed to open Internet Settings registry key");
                return;
            }

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);

            Logger.Information("System proxy cleared");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to clear system proxy");
        }
    }

    public void SetAutoStart(bool enable, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                Logger.Error("Failed to open Run registry key");
                return;
            }

            if (enable)
            {
                key.SetValue(AppName, $"\"{exePath}\"", RegistryValueKind.String);
                Logger.Information("Auto-start enabled for {ExePath}", exePath);
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                Logger.Information("Auto-start disabled");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to set auto-start (enable={Enable})", enable);
        }
    }

    public bool GetAutoStart(string appName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key is null)
            {
                Logger.Warning("Failed to open Run registry key for reading");
                return false;
            }

            var value = key.GetValue(appName);
            return value is not null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check auto-start for {AppName}", appName);
            return false;
        }
    }

    public string GetSystemLanguage()
    {
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    }

    public bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check administrator status");
            return false;
        }
    }

    public string GetAppDirectory()
    {
        return AppDomain.CurrentDomain.BaseDirectory;
    }
}
