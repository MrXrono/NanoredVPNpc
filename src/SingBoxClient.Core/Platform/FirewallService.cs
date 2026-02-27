using System.Diagnostics;
using System.Runtime.Versioning;
using Serilog;

namespace SingBoxClient.Core.Platform;

[SupportedOSPlatform("windows")]
public class FirewallService
{
    private const string RuleName = "NanoredVPN_SingBox";

    private static readonly ILogger Logger = Log.ForContext<FirewallService>();

    public void EnsureRules(string singBoxPath)
    {
        try
        {
            if (RuleExists())
            {
                Logger.Information("Firewall rules for {RuleName} already exist", RuleName);
                return;
            }

            AddRule(singBoxPath, "in");
            AddRule(singBoxPath, "out");

            Logger.Information(
                "Firewall rules created for sing-box at {SingBoxPath}", singBoxPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to ensure firewall rules for {SingBoxPath}", singBoxPath);
        }
    }

    public void RemoveRules()
    {
        try
        {
            RunNetsh(
                $"advfirewall firewall delete rule name=\"{RuleName}\"");

            Logger.Information("Firewall rules {RuleName} removed", RuleName);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to remove firewall rules {RuleName}", RuleName);
        }
    }

    private bool RuleExists()
    {
        try
        {
            var output = RunNetsh(
                $"advfirewall firewall show rule name=\"{RuleName}\"");

            return !output.Contains("No rules match the specified criteria",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void AddRule(string programPath, string direction)
    {
        var dirLabel = direction == "in" ? "Inbound" : "Outbound";

        RunNetsh(
            $"advfirewall firewall add rule " +
            $"name=\"{RuleName}\" " +
            $"dir={direction} " +
            $"action=allow " +
            $"program=\"{programPath}\" " +
            $"enable=yes " +
            $"profile=any " +
            $"description=\"{dirLabel} rule for NanoredVPN sing-box core\"");

        Logger.Debug("Added {Direction} firewall rule for {Program}", dirLabel, programPath);
    }

    private static string RunNetsh(string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        process.Start();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            Logger.Warning(
                "netsh exited with code {ExitCode}: {StdErr}",
                process.ExitCode, stderr.Trim());
        }

        return stdout;
    }
}
