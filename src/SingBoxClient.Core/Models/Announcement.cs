namespace SingBoxClient.Core.Models;

/// <summary>
/// Server-side notification or announcement displayed to the user.
/// </summary>
public class Announcement
{
    /// <summary>
    /// Unique announcement identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Announcement title / headline.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Full announcement body text (may contain markdown).
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// When the announcement was published (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Whether the user has acknowledged / read this announcement.
    /// </summary>
    public bool IsRead { get; set; } = false;
}
