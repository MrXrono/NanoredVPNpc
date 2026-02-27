namespace SingBoxClient.Core.Models;

/// <summary>
/// Supported proxy protocols.
/// </summary>
public enum Protocol
{
    VLESS = 0,
    VMess = 1,
    Trojan = 2,
    Shadowsocks = 3
}

/// <summary>
/// Represents a single proxy server node obtained from a subscription.
/// </summary>
public class ServerNode
{
    /// <summary>
    /// Proxy protocol used by this server.
    /// </summary>
    public Protocol Protocol { get; set; }

    /// <summary>
    /// Server hostname or IP address.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Server port number.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// UUID (for VLESS/VMess) or password (for Trojan/Shadowsocks).
    /// </summary>
    public string UuidOrPassword { get; set; } = string.Empty;

    /// <summary>
    /// TLS configuration for this server.
    /// </summary>
    public TlsSettings TlsSettings { get; set; } = new();

    /// <summary>
    /// Transport layer configuration (WebSocket, gRPC, etc.).
    /// </summary>
    public TransportSettings Transport { get; set; } = new();

    /// <summary>
    /// Human-readable server name (e.g. "DE-Frankfurt-01").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Last measured latency in milliseconds. -1 if not yet measured.
    /// </summary>
    public int Latency { get; set; } = -1;

    /// <summary>
    /// Whether the server responded to the last connectivity check.
    /// </summary>
    public bool IsReachable { get; set; } = false;

    /// <summary>
    /// Shadowsocks encryption method (e.g. "2022-blake3-aes-128-gcm", "aes-256-gcm").
    /// Only used when Protocol is Shadowsocks.
    /// </summary>
    public string ShadowsocksMethod { get; set; } = string.Empty;
}
