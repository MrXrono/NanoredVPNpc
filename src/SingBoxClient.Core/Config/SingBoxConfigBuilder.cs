using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Config;

/// <summary>
/// Main orchestrator that assembles a complete sing-box configuration JSON string
/// from all individual config sections: log, inbounds, outbounds, route, dns, experimental.
/// </summary>
public static class SingBoxConfigBuilder
{
    /// <summary>
    /// JSON serialization options matching sing-box's expected format:
    /// snake_case property names, indented output, null values omitted.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Builds the complete sing-box configuration as a JSON string.
    /// </summary>
    /// <param name="mode">Connection mode (Proxy or TUN).</param>
    /// <param name="proxyEnabled">Whether to include the mixed proxy inbound.</param>
    /// <param name="tunEnabled">Whether to include the TUN inbound.</param>
    /// <param name="proxyPort">Local proxy listen port (used when proxyEnabled is true).</param>
    /// <param name="server">The target proxy server node.</param>
    /// <param name="rules">Routing rules to apply (user + remote).</param>
    /// <param name="tunBypass">Process names excluded from TUN tunnel (bypass list).</param>
    /// <param name="tunProxy">Process names forced through TUN tunnel (include-only list).</param>
    /// <param name="debugMode">
    /// When true, sets log level to "debug" for verbose sing-box output.
    /// When false, uses "warn" for production operation.
    /// </param>
    /// <returns>
    /// A JSON string ready to be written to config.json and consumed by sing-box.
    /// </returns>
    public static string Build(
        ConnectionMode mode,
        bool proxyEnabled,
        bool tunEnabled,
        int proxyPort,
        ServerNode server,
        List<RoutingRule> rules,
        List<string>? tunBypass,
        List<string>? tunProxy,
        bool debugMode)
    {
        var config = new JsonObject();

        // --- Log ---
        config["log"] = new JsonObject
        {
            ["level"] = debugMode ? "debug" : "warn",
            ["timestamp"] = true
        };

        // --- Inbounds ---
        var inbounds = new JsonArray();

        if (proxyEnabled)
        {
            inbounds.Add(InboundConfig.BuildMixedProxy(proxyPort));
        }

        if (tunEnabled)
        {
            inbounds.Add(InboundConfig.BuildTun(tunProxy, tunBypass));
        }

        config["inbounds"] = inbounds;

        // --- Outbounds ---
        // Order matters: sing-box uses the first outbound as default if no route matches
        // Block and DNS outbounds replaced by route rule actions (sing-box 1.11+):
        // "block" → route rule "action":"reject", "dns-out" → route rule "action":"hijack-dns"
        var outbounds = new JsonArray
        {
            OutboundConfig.BuildServerOutbound(server),
            OutboundConfig.BuildDirect()
        };

        config["outbounds"] = outbounds;

        // --- Route ---
        config["route"] = RouteConfig.Build(rules);

        // --- DNS ---
        // TUN mode requires FakeIP for proper traffic interception
        bool useFakeIp = tunEnabled;
        config["dns"] = DnsConfig.Build(useFakeIp);

        // --- Experimental ---
        config["experimental"] = ExperimentalConfig.Build();

        return config.ToJsonString(SerializerOptions);
    }
}
