using System.Text.Json.Serialization;

namespace SingBoxClient.Core.Models;

/// <summary>
/// Types of routing rules supported by sing-box.
/// </summary>
public enum RuleType
{
    Domain = 0,
    DomainSuffix = 1,
    IpCidr = 2,
    GeoIP = 3,
    GeoSite = 4
}

/// <summary>
/// Action to take when a routing rule matches.
/// </summary>
public enum RuleAction
{
    Proxy = 0,
    Direct = 1,
    Block = 2
}

/// <summary>
/// A single routing rule that determines how traffic is handled.
/// </summary>
public class RoutingRule
{
    /// <summary>
    /// Unique rule identifier (GUID string).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Rule matching type.
    /// </summary>
    [JsonPropertyName("type")]
    public RuleType Type { get; set; }

    /// <summary>
    /// Value to match against (domain, IP CIDR, geo code, etc.).
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Action to take when the rule matches.
    /// </summary>
    [JsonPropertyName("action")]
    public RuleAction Action { get; set; } = RuleAction.Proxy;

    /// <summary>
    /// Whether this rule was fetched from a remote configuration.
    /// </summary>
    [JsonPropertyName("is_remote")]
    public bool IsRemote { get; set; } = false;

    /// <summary>
    /// Whether this rule is currently active.
    /// </summary>
    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Rule evaluation priority (lower = higher priority).
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 100;
}
