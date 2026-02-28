using System.Text.Json.Nodes;

namespace SingBoxClient.Core.Config;

/// <summary>
/// Builds the sing-box DNS configuration section.
/// Uses new DNS server format (sing-box 1.12+) with typed servers and address_resolver.
/// Supports FakeIP mode for TUN operation.
/// </summary>
public static class DnsConfig
{
    /// <summary>
    /// Builds the DNS configuration with typed DoH servers and routing rules.
    /// </summary>
    /// <param name="useFakeIp">
    /// When true, enables FakeIP for TUN mode. A synthetic IP range (198.18.0.0/15)
    /// is assigned to DNS responses, allowing sing-box to intercept connections by IP
    /// and route them through the correct outbound. Required for proper TUN operation.
    /// </param>
    /// <returns>JsonObject representing the dns section of sing-box config.</returns>
    public static JsonObject Build(bool useFakeIp = false)
    {
        // --- DNS Servers (new typed format, sing-box 1.12+) ---
        var servers = new JsonArray();

        // Primary: Google DoH routed through the proxy tunnel
        servers.Add(new JsonObject
        {
            ["tag"] = "google-doh",
            ["type"] = "https",
            ["server"] = "dns.google",
            ["server_port"] = 443,
            ["detour"] = "proxy",
            ["address_resolver"] = "direct-dns"
        });

        // Fallback: Direct UDP DNS for local/private domain resolution and DoH bootstrap
        servers.Add(new JsonObject
        {
            ["tag"] = "direct-dns",
            ["type"] = "udp",
            ["server"] = "223.5.5.5",
            ["detour"] = "direct"
        });

        // FakeIP server for TUN mode — assigns synthetic IPs from a reserved range
        if (useFakeIp)
        {
            servers.Add(new JsonObject
            {
                ["tag"] = "fakeip",
                ["type"] = "fakeip",
                ["inet4_range"] = "198.18.0.0/15"
            });
        }

        // --- DNS Rules ---
        // Rule evaluation order matters: first match wins.
        var rules = new JsonArray();

        // 1. Route queries for private/local domain suffixes to direct DNS
        rules.Add(new JsonObject
        {
            ["domain_suffix"] = new JsonArray
            {
                ".local",
                ".localhost",
                ".internal",
                ".lan"
            },
            ["server"] = "direct-dns"
        });

        // 2. In FakeIP mode, intercept A/AAAA queries with synthetic IPs
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

        return dns;
    }
}
