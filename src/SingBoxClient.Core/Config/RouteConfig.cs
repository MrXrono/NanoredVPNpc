using System.Text.Json.Nodes;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Config;

/// <summary>
/// Builds the sing-box route section from user and remote routing rules.
/// Converts <see cref="RoutingRule"/> objects into sing-box native JSON rule format.
/// </summary>
public static class RouteConfig
{
    /// <summary>
    /// Builds the complete route configuration object.
    /// Rule evaluation order: DNS interception -> user/remote rules -> private IP direct -> final proxy.
    /// </summary>
    /// <param name="rules">
    /// List of routing rules to include. Only enabled rules are processed.
    /// Rules are sorted by priority (lower value = higher priority).
    /// </param>
    /// <returns>JsonObject representing the route section of sing-box config.</returns>
    public static JsonObject Build(List<RoutingRule> rules)
    {
        var rulesArray = new JsonArray();

        // DNS protocol interception — route all DNS queries to the sing-box DNS engine
        rulesArray.Add(new JsonObject
        {
            ["protocol"] = "dns",
            ["outbound"] = "dns-out"
        });

        // Private/internal IP ranges — always go direct to avoid routing loops
        rulesArray.Add(new JsonObject
        {
            ["ip_is_private"] = true,
            ["outbound"] = "direct"
        });

        // Group enabled rules by (type + action) to merge values into single rules
        // This produces compact config: e.g. one rule with domain_suffix: [".ru", ".su"]
        // instead of separate rules per domain
        var grouped = rules
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.Priority)
            .GroupBy(r => new { r.Type, r.Action });

        foreach (var group in grouped)
        {
            var ruleObj = new JsonObject();
            var values = new JsonArray();

            foreach (var rule in group)
            {
                values.Add(rule.Value);
            }

            string fieldName = group.Key.Type switch
            {
                RuleType.Domain => "domain",
                RuleType.DomainSuffix => "domain_suffix",
                RuleType.IpCidr => "ip_cidr",
                RuleType.GeoIP => "geoip",
                RuleType.GeoSite => "geosite",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(rules),
                    $"Unsupported rule type: {group.Key.Type}")
            };

            ruleObj[fieldName] = values;
            ruleObj["outbound"] = group.Key.Action.ToString().ToLower();

            rulesArray.Add(ruleObj);
        }

        return new JsonObject
        {
            ["rules"] = rulesArray,
            ["auto_detect_interface"] = true,
            ["final"] = "proxy"
        };
    }
}
