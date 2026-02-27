namespace SingBoxClient.Core.Models;

/// <summary>
/// Subscription metadata received from the provider.
/// </summary>
public class SubscriptionData
{
    /// <summary>
    /// Unique subscription identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Subscription expiration date (UTC).
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Total traffic allowance in bytes.
    /// </summary>
    public long TotalTraffic { get; set; }

    /// <summary>
    /// Traffic already consumed in bytes.
    /// </summary>
    public long UsedTraffic { get; set; }

    /// <summary>
    /// How often the subscription should be refreshed, in hours.
    /// </summary>
    public int UpdateInterval { get; set; } = 24;

    /// <summary>
    /// Human-readable profile/subscription title.
    /// </summary>
    public string ProfileTitle { get; set; } = string.Empty;
}
