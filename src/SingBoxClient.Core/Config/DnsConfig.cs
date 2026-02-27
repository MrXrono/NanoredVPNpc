using System.Text.Json.Nodes;

namespace SingBoxClient.Core.Config;

/// <summary>
/// Builds the sing-box DNS configuration section.
/// Uses DNS-over-HTTPS (DoH) for proxied queries and a direct DNS for local resolution.
/// Supports FakeIP mode for TUN operation.
/// </summary>
public static class DnsConfig
{
    /// <summary>
    /// Builds the DNS configuration with DoH servers and routing rules.
    /// </summary>
    /// <param name="useFakeIp">
    /// When true, enables FakeIP for TUN mode. A synthetic IP range (198.18.0.0/15)
    /// is assigned to DNS responses, allowing sing-box to intercept connections by IP
    /// and route them through the correct outbound. Required for proper TUN operation.
    /// </param>
    /// <returns>JsonObject representing the dns section of sing-box config.</returns>
    public static JsonObject Build(bool useFakeIp = false)
    {
        // --- DNS Servers ---
        var servers = new JsonArray();

        // Primary: Google DoH routed through the proxy tunnel
        servers.Add(new JsonObject
        {
            ["tag"] = "google-doh",
            ["address"] = "https://dns.google/dns-query",
            ["detour"] = "proxy"
        });

        // Fallback: Direct DNS for local/private domain resolution
        servers.Add(new JsonObject
        {
            ["tag"] = "direct-dns",
            ["address"] = "223.5.5.5",
            ["detour"] = "direct"
        });

        // FakeIP server for TUN mode — assigns synthetic IPs from a reserved range
        if (useFakeIp)
        {
            servers.Add(new JsonObject
            {
                ["tag"] = "fakeip",
                ["address"] = "fakeip"
            });
        }

        // --- DNS Rules ---
        var rules = new JsonArray();

        // Private/local domains always resolve via direct DNS
        rules.Add(new JsonObject
        {
            ["outbound"] = new JsonArray { "any" },
            ["server"] = "direct-dns"
        });

        // Route queries for private domain suffixes to direct DNS
        rules.Add(new JsonObject
        {
            ["domain_suffix"] = new JsonArray
            {
                ".local",
                ".localhost",
                ".internal"
            },
            ["server"] = "direct-dns"
        });

        // In FakeIP mode, all non-direct queries get fake IPs for interception
        if (useFakeIp)
        {
            rules.Add(new JsonObject
            {
                ["query_type"] = new JsonArray { "A", "AAAA" },
                ["server"] = "fakeip"
            });
        }

        // --- Assemble DNS config ---
        var dns = new JsonObject
        {
            ["servers"] = servers,
            ["rules"] = rules,
            ["final"] = "google-doh",
            ["independent_cache"] = true
        };

        // FakeIP configuration block
        if (useFakeIp)
        {
            dns["fakeip"] = new JsonObject
            {
                ["enabled"] = true,
                ["inet4_range"] = "198.18.0.0/15"
            };
        }

        return dns;
    }
}
