namespace SingBoxClient.Core.Constants;

/// <summary>
/// Predefined DNS server configurations used for sing-box routing.
/// </summary>
public static class DnsPresets
{
    /// <summary>
    /// Default DoH (DNS-over-HTTPS) upstream servers.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDohServers = new[]
    {
        "https://dns.google/dns-query",
        "https://cloudflare-dns.com/dns-query",
        "https://dns.adguard-dns.com/dns-query"
    };

    /// <summary>
    /// CIDR range used by sing-box for Fake-IP DNS resolution.
    /// Packets destined for this range are intercepted and mapped to real addresses.
    /// </summary>
    public const string FakeIpRange = "198.18.0.0/15";

    /// <summary>
    /// Fallback plain-DNS server used for local / direct queries
    /// (e.g. resolving domestic domains before the tunnel is established).
    /// </summary>
    public const string LocalDnsServer = "223.5.5.5";
}
