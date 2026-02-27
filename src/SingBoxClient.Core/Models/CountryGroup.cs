namespace SingBoxClient.Core.Models;

/// <summary>
/// Groups servers by country for UI display and selection.
/// </summary>
public class CountryGroup
{
    /// <summary>
    /// ISO 3166-1 alpha-2 country code (e.g. "DE", "NL", "US").
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Localized display name with flag emoji (e.g. "🇩🇪 Germany").
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// All servers located in this country.
    /// </summary>
    public List<ServerNode> Servers { get; set; } = new();

    /// <summary>
    /// Server with the lowest latency in this country group, or null if none reachable.
    /// </summary>
    public ServerNode? BestServer { get; set; }

    /// <summary>
    /// Average latency across all reachable servers in this group, in milliseconds.
    /// </summary>
    public int AverageLatency { get; set; }
}
