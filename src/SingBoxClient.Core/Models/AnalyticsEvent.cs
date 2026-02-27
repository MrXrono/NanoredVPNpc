namespace SingBoxClient.Core.Models;

/// <summary>
/// A single analytics event for telemetry / usage tracking.
/// </summary>
public class AnalyticsEvent
{
    /// <summary>
    /// Event name identifier (e.g. "connection_started", "server_switched").
    /// </summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>
    /// Arbitrary key-value properties associated with this event.
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = new();

    /// <summary>
    /// When the event occurred (UTC).
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
