using System.Text.Json.Nodes;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Config;

/// <summary>
/// Builds sing-box outbound configuration objects.
/// Supports VLESS, VMess, Trojan, and Shadowsocks protocols with TLS and transport layers.
/// </summary>
public static class OutboundConfig
{
    /// <summary>
    /// Builds the main server outbound based on the proxy protocol of the given <see cref="ServerNode"/>.
    /// Dispatches to protocol-specific builders for VLESS, VMess, Trojan, or Shadowsocks.
    /// </summary>
    /// <param name="server">Server node containing protocol, address, credentials, TLS, and transport settings.</param>
    /// <returns>JsonObject representing the fully configured server outbound.</returns>
    public static JsonObject BuildServerOutbound(ServerNode server)
    {
        return server.Protocol switch
        {
            Protocol.VLESS => BuildVless(server),
            Protocol.VMess => BuildVmess(server),
            Protocol.Trojan => BuildTrojan(server),
            Protocol.Shadowsocks => BuildShadowsocks(server),
            _ => throw new ArgumentOutOfRangeException(
                nameof(server),
                $"Unsupported protocol: {server.Protocol}")
        };
    }

    /// <summary>
    /// Builds a direct outbound that sends traffic without any proxy.
    /// </summary>
    public static JsonObject BuildDirect()
    {
        return new JsonObject
        {
            ["type"] = "direct",
            ["tag"] = "direct"
        };
    }

    /// <summary>
    /// Builds a block outbound that silently drops all matched traffic.
    /// </summary>
    public static JsonObject BuildBlock()
    {
        return new JsonObject
        {
            ["type"] = "block",
            ["tag"] = "block"
        };
    }

    /// <summary>
    /// Builds a DNS outbound used for routing DNS queries through the sing-box DNS engine.
    /// </summary>
    public static JsonObject BuildDns()
    {
        return new JsonObject
        {
            ["type"] = "dns",
            ["tag"] = "dns-out"
        };
    }

    #region Protocol Builders

    private static JsonObject BuildVless(ServerNode server)
    {
        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = "proxy",
            ["server"] = server.Address,
            ["server_port"] = server.Port,
            ["uuid"] = server.UuidOrPassword
        };

        // VLESS + REALITY requires xtls-rprx-vision flow
        if (server.TlsSettings.IsReality)
        {
            outbound["flow"] = "xtls-rprx-vision";
        }

        ApplyTls(outbound, server.TlsSettings);
        ApplyTransport(outbound, server.Transport);

        return outbound;
    }

    private static JsonObject BuildVmess(ServerNode server)
    {
        var outbound = new JsonObject
        {
            ["type"] = "vmess",
            ["tag"] = "proxy",
            ["server"] = server.Address,
            ["server_port"] = server.Port,
            ["uuid"] = server.UuidOrPassword,
            ["alter_id"] = 0
        };

        ApplyTls(outbound, server.TlsSettings);
        ApplyTransport(outbound, server.Transport);

        return outbound;
    }

    private static JsonObject BuildTrojan(ServerNode server)
    {
        var outbound = new JsonObject
        {
            ["type"] = "trojan",
            ["tag"] = "proxy",
            ["server"] = server.Address,
            ["server_port"] = server.Port,
            ["password"] = server.UuidOrPassword
        };

        ApplyTls(outbound, server.TlsSettings);
        ApplyTransport(outbound, server.Transport);

        return outbound;
    }

    private static JsonObject BuildShadowsocks(ServerNode server)
    {
        var outbound = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["tag"] = "proxy",
            ["server"] = server.Address,
            ["server_port"] = server.Port,
            ["method"] = server.ShadowsocksMethod,
            ["password"] = server.UuidOrPassword
        };

        return outbound;
    }

    #endregion

    #region TLS & Transport Helpers

    /// <summary>
    /// Applies TLS settings to the outbound object.
    /// Includes uTLS fingerprint, ALPN, insecure flag, and REALITY if configured.
    /// </summary>
    private static void ApplyTls(JsonObject outbound, TlsSettings tls)
    {
        // Skip TLS entirely if no server name is configured
        if (string.IsNullOrEmpty(tls.ServerName))
            return;

        var tlsObj = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = tls.ServerName
        };

        // uTLS browser fingerprint (required for REALITY)
        var fingerprint = !string.IsNullOrEmpty(tls.Fingerprint) ? tls.Fingerprint
            : tls.IsReality ? "chrome" : null;

        if (fingerprint != null)
        {
            tlsObj["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = fingerprint
            };
        }

        // ALPN negotiation protocols
        if (tls.Alpn is { Count: > 0 })
        {
            var alpnArr = new JsonArray();
            foreach (var proto in tls.Alpn)
                alpnArr.Add(proto);
            tlsObj["alpn"] = alpnArr;
        }

        // Certificate verification bypass
        if (tls.AllowInsecure)
        {
            tlsObj["insecure"] = true;
        }

        // REALITY TLS extension
        if (tls.IsReality)
        {
            tlsObj["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = tls.RealityPublicKey,
                ["short_id"] = tls.RealityShortId
            };
        }

        outbound["tls"] = tlsObj;
    }

    /// <summary>
    /// Applies transport layer settings (WebSocket, gRPC, HTTP) to the outbound object.
    /// </summary>
    private static void ApplyTransport(JsonObject outbound, TransportSettings transport)
    {
        // TCP is the default transport in sing-box — no explicit section needed
        if (string.IsNullOrEmpty(transport.Type) ||
            transport.Type.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            return;

        var transportObj = new JsonObject
        {
            ["type"] = transport.Type
        };

        switch (transport.Type.ToLowerInvariant())
        {
            case "ws":
                if (!string.IsNullOrEmpty(transport.Path))
                    transportObj["path"] = transport.Path;

                if (!string.IsNullOrEmpty(transport.Host))
                {
                    transportObj["headers"] = new JsonObject
                    {
                        ["Host"] = transport.Host
                    };
                }
                break;

            case "grpc":
                if (!string.IsNullOrEmpty(transport.ServiceName))
                    transportObj["service_name"] = transport.ServiceName;
                break;

            case "http":
                if (!string.IsNullOrEmpty(transport.Host))
                {
                    transportObj["host"] = new JsonArray { transport.Host };
                }
                if (!string.IsNullOrEmpty(transport.Path))
                    transportObj["path"] = transport.Path;
                break;

            case "httpupgrade":
                if (!string.IsNullOrEmpty(transport.Path))
                    transportObj["path"] = transport.Path;
                if (!string.IsNullOrEmpty(transport.Host))
                    transportObj["host"] = transport.Host;
                break;
        }

        outbound["transport"] = transportObj;
    }

    #endregion
}
