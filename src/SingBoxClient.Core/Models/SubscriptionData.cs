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
    /// Subscription expiration date (UTC). Null if the provider did not send expiration info.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Total traffic allowance in bytes. 0 means unlimited.
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

    /// <summary>
    /// Support URL from the provider (support-url header).
    /// </summary>
    public string SupportUrl { get; set; } = string.Empty;

    /// <summary>
    /// Subscription management web page URL (profile-web-page-url header).
    /// </summary>
    public string WebPageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Announcement text from the provider (announce header).
    /// </summary>
    public string Announce { get; set; } = string.Empty;

    /// <summary>
    /// True if the subscription has expired.
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow;

    /// <summary>
    /// True if traffic is unlimited (total = 0).
    /// </summary>
    public bool IsUnlimitedTraffic => TotalTraffic <= 0;
}
