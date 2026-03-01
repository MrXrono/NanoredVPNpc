using System.Text.Json.Nodes;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Config;

/// <summary>
/// Builds the sing-box route section from user and remote routing rules.
/// Converts <see cref="RoutingRule"/> objects into sing-box native JSON rule format.
/// GeoIP/GeoSite rules are emitted as rule_set references (sing-box v1.8+).
/// </summary>
public static class RouteConfig
{
    /// <summary>
    /// sing-box rule-set source URL template for SagerNet community sets.
    /// </summary>
    private const string RuleSetUrlTemplate =
        "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/{0}.srs";

    private const string GeoIpRuleSetUrlTemplate =
        "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/{0}.srs";

    /// <summary>
    /// Builds the complete route configuration object.
    /// Rule evaluation order: DNS interception → user/remote rules → private IP direct → final proxy.
    /// </summary>
    public static JsonObject Build(List<RoutingRule> rules)
    {
        var rulesArray = new JsonArray();
        var ruleSets = new JsonArray();
        var ruleSetTags = new HashSet<string>();

        // Protocol sniffing — replaces deprecated inbound.sniff fields (sing-box 1.11+)
        rulesArray.Add(new JsonObject
        {
            ["action"] = "sniff",
            ["timeout"] = "300ms"
        });

        // DNS protocol interception — hijack DNS queries to the sing-box DNS engine (rule action, sing-box 1.11+)
        rulesArray.Add(new JsonObject
        {
            ["protocol"] = "dns",
            ["action"] = "hijack-dns"
        });

        // Private/internal IP ranges — always go direct to avoid routing loops
        rulesArray.Add(new JsonObject
        {
            ["ip_is_private"] = true,
            ["outbound"] = "direct"
        });

        // Group enabled rules by (type + action) to merge values into single rules
        var grouped = rules
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.Priority)
            .GroupBy(r => new { r.Type, r.Action });

        foreach (var group in grouped)
        {
            var type = group.Key.Type;
            var action = group.Key.Action;

            // GeoIP / GeoSite / RuleSet → emit as rule_set references
            if (type is RuleType.GeoIP or RuleType.GeoSite or RuleType.RuleSet)
            {
                var tags = new JsonArray();

                foreach (var rule in group)
                {
                    var tag = $"rs-{type.ToString().ToLower()}-{rule.Value}";

                    tags.Add(tag);

                    // Register rule_set source (deduplicated)
                    if (ruleSetTags.Add(tag))
                    {
                        var url = type == RuleType.GeoIP
                            ? string.Format(GeoIpRuleSetUrlTemplate, rule.Value)
                            : type == RuleType.GeoSite
                                ? string.Format(RuleSetUrlTemplate, rule.Value)
                                : rule.Value; // RuleSet type: Value is the full URL

                        ruleSets.Add(new JsonObject
                        {
                            ["tag"] = tag,
                            ["type"] = "remote",
                            ["format"] = "binary",
                            ["url"] = url,
                            ["download_detour"] = "proxy"
                        });
                    }
                }

                var ruleSetRule = new JsonObject { ["rule_set"] = tags };
                ApplyRuleAction(ruleSetRule, action);
                rulesArray.Add(ruleSetRule);
            }
            else
            {
                // Domain, DomainSuffix, IpCidr → standard inline rules
                var ruleObj = new JsonObject();
                var values = new JsonArray();

                foreach (var rule in group)
                {
                    values.Add(rule.Value);
                }

                string fieldName = type switch
                {
                    RuleType.Domain => "domain",
                    RuleType.DomainSuffix => "domain_suffix",
                    RuleType.IpCidr => "ip_cidr",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(rules),
                        $"Unsupported rule type: {type}")
                };

                ruleObj[fieldName] = values;
                ApplyRuleAction(ruleObj, action);

                rulesArray.Add(ruleObj);
            }
        }

        var route = new JsonObject
        {
            ["rules"] = rulesArray,
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "direct-dns",
            ["final"] = "proxy"
        };

        // Attach rule_set definitions if any were generated
        if (ruleSets.Count > 0)
        {
            route["rule_set"] = ruleSets;
        }

        return route;
    }

    /// <summary>
    /// Applies the correct action/outbound to a route rule object.
    /// Block uses "action":"reject" (sing-box 1.11+ rule action, replacing legacy block outbound).
    /// Proxy/Direct use traditional "outbound" field.
    /// </summary>
    private static void ApplyRuleAction(JsonObject ruleObj, RuleAction action)
    {
        if (action == RuleAction.Block)
        {
            ruleObj["action"] = "reject";
        }
        else
        {
            ruleObj["outbound"] = action.ToString().ToLower();
        }
    }
}
