using System.Text.Json;
using System.Web;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Helpers;

/// <summary>
/// Parses VPN share links (vless://, vmess://, trojan://, ss://) into <see cref="ServerNode"/> objects.
/// </summary>
public static class ShareLinkParser
{
    /// <summary>
    /// Parse a single share link into a <see cref="ServerNode"/>.
    /// </summary>
    /// <returns>Parsed node or <c>null</c> if the link is not recognized.</returns>
    public static ServerNode? Parse(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
            return null;

        link = link.Trim();

        try
        {
            if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                return ParseVless(link);

            if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                return ParseVmess(link);

            if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                return ParseTrojan(link);

            if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
                return ParseShadowsocks(link);
        }
        catch
        {
            // Malformed link -- return null
        }

        return null;
    }

    /// <summary>
    /// Split <paramref name="content"/> by newlines and parse every recognised share link.
    /// </summary>
    public static List<ServerNode> ParseMultiple(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new List<ServerNode>();

        // The content may itself be a single Base64 blob (subscription body).
        if (!content.Contains("://"))
        {
            var decoded = Base64Helper.Decode(content);
            if (decoded.Contains("://"))
                content = decoded;
        }

        var results = new List<ServerNode>();

        foreach (var rawLine in content.Split('\n', '\r'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            var node = Parse(line);
            if (node is not null)
                results.Add(node);
        }

        return results;
    }

    // ── VLESS ────────────────────────────────────────────────────────────

    private static ServerNode? ParseVless(string link)
    {
        // vless://uuid@host:port?params#name
        var uri = new Uri(link);
        var qs = HttpUtility.ParseQueryString(uri.Query);

        return new ServerNode
        {
            Protocol = Protocol.VLESS,
            UuidOrPassword = uri.UserInfo,
            Address = uri.Host,
            Port = uri.Port,
            Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            TlsSettings = BuildTls(qs),
            Transport = BuildTransport(qs),
        };
    }

    // ── VMess ────────────────────────────────────────────────────────────

    private static ServerNode? ParseVmess(string link)
    {
        // vmess://base64(json)
        var payload = link["vmess://".Length..];
        var json = Base64Helper.Decode(payload);
        if (string.IsNullOrEmpty(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var node = new ServerNode
        {
            Protocol = Protocol.VMess,
            Address = GetJsonString(root, "add"),
            Port = GetJsonInt(root, "port"),
            UuidOrPassword = GetJsonString(root, "id"),
            Name = GetJsonString(root, "ps"),
        };

        // TLS
        var tls = new TlsSettings
        {
            ServerName = GetJsonString(root, "sni"),
            Fingerprint = GetJsonString(root, "fp"),
        };

        var tlsField = GetJsonString(root, "tls");
        if (string.IsNullOrEmpty(tls.ServerName) && !string.IsNullOrEmpty(GetJsonString(root, "host")))
            tls.ServerName = GetJsonString(root, "host");

        node.TlsSettings = tls;

        // Transport
        var transport = new TransportSettings
        {
            Type = MapNetworkType(GetJsonString(root, "net")),
            Path = GetJsonString(root, "path"),
            Host = GetJsonString(root, "host"),
        };

        node.Transport = transport;
        return node;
    }

    // ── Trojan ───────────────────────────────────────────────────────────

    private static ServerNode? ParseTrojan(string link)
    {
        // trojan://password@host:port?params#name
        var uri = new Uri(link);
        var qs = HttpUtility.ParseQueryString(uri.Query);

        return new ServerNode
        {
            Protocol = Protocol.Trojan,
            UuidOrPassword = uri.UserInfo,
            Address = uri.Host,
            Port = uri.Port,
            Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            TlsSettings = BuildTls(qs),
            Transport = BuildTransport(qs),
        };
    }

    // ── Shadowsocks ──────────────────────────────────────────────────────

    private static ServerNode? ParseShadowsocks(string link)
    {
        // Format 1: ss://base64(method:password)@host:port#name
        // Format 2: ss://base64(method:password@host:port)#name

        var afterScheme = link["ss://".Length..];

        // Split off fragment (name)
        string name = string.Empty;
        var hashIdx = afterScheme.LastIndexOf('#');
        if (hashIdx >= 0)
        {
            name = Uri.UnescapeDataString(afterScheme[(hashIdx + 1)..]);
            afterScheme = afterScheme[..hashIdx];
        }

        string method;
        string password;
        string host;
        int port;

        var atIdx = afterScheme.IndexOf('@');
        if (atIdx >= 0)
        {
            // Format 1: base64(method:password)@host:port
            var userInfo = Base64Helper.Decode(afterScheme[..atIdx]);
            if (string.IsNullOrEmpty(userInfo))
                userInfo = afterScheme[..atIdx]; // not encoded

            var colonIdx = userInfo.IndexOf(':');
            if (colonIdx < 0) return null;

            method = userInfo[..colonIdx];
            password = userInfo[(colonIdx + 1)..];

            var hostPort = afterScheme[(atIdx + 1)..];
            ParseHostPort(hostPort, out host, out port);
        }
        else
        {
            // Format 2: base64(method:password@host:port)
            var decoded = Base64Helper.Decode(afterScheme);
            if (string.IsNullOrEmpty(decoded))
                return null;

            var lastAt = decoded.LastIndexOf('@');
            if (lastAt < 0) return null;

            var userInfo = decoded[..lastAt];
            var colonIdx = userInfo.IndexOf(':');
            if (colonIdx < 0) return null;

            method = userInfo[..colonIdx];
            password = userInfo[(colonIdx + 1)..];

            ParseHostPort(decoded[(lastAt + 1)..], out host, out port);
        }

        return new ServerNode
        {
            Protocol = Protocol.Shadowsocks,
            Address = host,
            Port = port,
            // Store method:password -- the config builder will split them
            UuidOrPassword = $"{method}:{password}",
            Name = name,
        };
    }

    // ── Shared helpers ───────────────────────────────────────────────────

    private static TlsSettings BuildTls(System.Collections.Specialized.NameValueCollection qs)
    {
        var tls = new TlsSettings
        {
            ServerName = qs["sni"] ?? string.Empty,
            Fingerprint = qs["fp"] ?? string.Empty,
        };

        var security = qs["security"] ?? string.Empty;
        if (security.Equals("reality", StringComparison.OrdinalIgnoreCase))
        {
            // Reality-specific: pbk (public key) and sid (short id) are stored in SNI/Fingerprint fields
            // The config builder will handle them.
            if (!string.IsNullOrEmpty(qs["pbk"]))
                tls.Fingerprint = qs["fp"] ?? string.Empty;
        }

        return tls;
    }

    private static TransportSettings BuildTransport(System.Collections.Specialized.NameValueCollection qs)
    {
        return new TransportSettings
        {
            Type = MapNetworkType(qs["type"] ?? string.Empty),
            Path = qs["path"] ?? string.Empty,
            Host = qs["host"] ?? string.Empty,
            ServiceName = qs["serviceName"] ?? string.Empty,
        };
    }

    /// <summary>
    /// Normalise the "net" / "type" value from share links to the sing-box transport name.
    /// </summary>
    private static string MapNetworkType(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "ws" or "websocket" => "ws",
            "grpc" or "gun" => "grpc",
            "http" or "h2" => "http",
            "httpupgrade" => "httpupgrade",
            "quic" => "quic",
            "tcp" => "tcp",
            _ => raw.ToLowerInvariant(),
        };
    }

    private static void ParseHostPort(string hostPort, out string host, out int port)
    {
        port = 0;
        host = hostPort;

        var lastColon = hostPort.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(hostPort[(lastColon + 1)..], out var p))
        {
            host = hostPort[..lastColon];
            port = p;
        }
    }

    private static string GetJsonString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            return val.ValueKind switch
            {
                JsonValueKind.String => val.GetString() ?? string.Empty,
                JsonValueKind.Number => val.GetRawText(),
                _ => string.Empty,
            };
        }
        return string.Empty;
    }

    private static int GetJsonInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number)
                return val.GetInt32();

            if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var n))
                return n;
        }
        return 0;
    }
}
