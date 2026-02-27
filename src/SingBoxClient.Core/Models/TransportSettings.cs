namespace SingBoxClient.Core.Models;

/// <summary>
/// Transport layer configuration (WebSocket, gRPC, HTTP, etc.).
/// </summary>
public class TransportSettings
{
    /// <summary>
    /// Transport type: "ws", "grpc", "http", "httpupgrade", "quic".
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Path for WebSocket / HTTP transport.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Host header value for WebSocket / HTTP transport.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// gRPC service name.
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;
}
