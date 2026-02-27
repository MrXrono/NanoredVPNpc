namespace SingBoxClient.Core.Models;

/// <summary>
/// Result of a latency check against a single server.
/// </summary>
public class PingResult
{
    /// <summary>
    /// The server that was pinged.
    /// </summary>
    public ServerNode Server { get; set; } = new();

    /// <summary>
    /// Measured round-trip latency in milliseconds. -1 if unreachable.
    /// </summary>
    public int LatencyMs { get; set; } = -1;

    /// <summary>
    /// Whether the server responded within the timeout window.
    /// </summary>
    public bool IsReachable { get; set; } = false;
}
