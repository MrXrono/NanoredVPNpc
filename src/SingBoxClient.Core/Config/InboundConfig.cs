using System.Text.Json.Nodes;

namespace SingBoxClient.Core.Config;

/// <summary>
/// Builds sing-box inbound configuration objects.
/// Supports mixed proxy (SOCKS5+HTTP) and TUN inbound types.
/// </summary>
public static class InboundConfig
{
    /// <summary>
    /// Builds a mixed proxy inbound (SOCKS5 + HTTP on the same port).
    /// Used in Proxy connection mode for system-level proxy forwarding.
    /// </summary>
    /// <param name="port">Local listen port (default 2080).</param>
    /// <returns>JsonObject representing the mixed inbound configuration.</returns>
    public static JsonObject BuildMixedProxy(int port)
    {
        return new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "mixed-in",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = port
        };
    }

    /// <summary>
    /// Builds a TUN inbound for capturing all system traffic via a virtual network adapter.
    /// Supports per-application routing via include/exclude process lists.
    /// </summary>
    /// <param name="includeProcess">
    /// If non-null and non-empty, only these process names will be tunneled.
    /// Mutually exclusive with <paramref name="excludeProcess"/>.
    /// </param>
    /// <param name="excludeProcess">
    /// If non-null and non-empty, these process names will bypass the tunnel.
    /// Mutually exclusive with <paramref name="includeProcess"/>.
    /// </param>
    /// <returns>JsonObject representing the TUN inbound configuration.</returns>
    public static JsonObject BuildTun(List<string>? includeProcess, List<string>? excludeProcess)
    {
        var tun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = "tun-in",
            ["auto_route"] = true,
            ["strict_route"] = true,
            ["inet4_address"] = "172.19.0.1/30",
            ["stack"] = "system"
        };

        if (includeProcess is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var proc in includeProcess)
                arr.Add(proc);
            tun["include_process"] = arr;
        }

        if (excludeProcess is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var proc in excludeProcess)
                arr.Add(proc);
            tun["exclude_process"] = arr;
        }

        return tun;
    }
}
