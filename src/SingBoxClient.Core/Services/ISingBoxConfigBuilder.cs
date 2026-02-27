using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Builds sing-box JSON configuration files for a given server and writes them to disk.
/// </summary>
public interface ISingBoxConfigBuilder
{
    /// <summary>
    /// Build a complete sing-box configuration for the given server and write it to disk.
    /// </summary>
    /// <returns>Absolute path to the generated config.json file.</returns>
    string BuildAndSave(ServerNode server);
}

/// <summary>
/// Default implementation that delegates to the static <see cref="Config.SingBoxConfigBuilder"/>
/// and persists the result to disk.
/// </summary>
public class SingBoxConfigBuilderService : ISingBoxConfigBuilder
{
    private readonly ISettingsService _settings;
    private readonly IRoutingService _routing;

    public SingBoxConfigBuilderService(ISettingsService settings, IRoutingService routing)
    {
        _settings = settings;
        _routing = routing;
    }

    public string BuildAndSave(ServerNode server)
    {
        var s = _settings.Settings;
        var rules = _routing.GetRules();

        var json = Config.SingBoxConfigBuilder.Build(
            mode: s.TunEnabled ? ConnectionMode.TUN : ConnectionMode.Proxy,
            proxyEnabled: s.ProxyEnabled,
            tunEnabled: s.TunEnabled,
            proxyPort: s.ProxyPort,
            server: server,
            rules: rules,
            tunBypass: s.TunBypassApps,
            tunProxy: s.TunProxyApps,
            debugMode: s.DebugMode);

        var dataDir = Constants.AppDefaults.DataDir;
        Directory.CreateDirectory(dataDir);
        var configPath = Path.Combine(dataDir, Constants.AppDefaults.ConfigFileName);
        File.WriteAllText(configPath, json);

        return Path.GetFullPath(configPath);
    }
}
