namespace SingBoxClient.Core.Models;

/// <summary>
/// Information about an available application or sing-box core update.
/// </summary>
public class UpdateInfo
{
    /// <summary>
    /// Whether a newer version is available.
    /// </summary>
    public bool Available { get; set; } = false;

    /// <summary>
    /// Version string of the available update (e.g. "1.2.0").
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Direct download URL for the application update package.
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Direct download URL for the sing-box core binary update, or null if no core update.
    /// </summary>
    public string? SingBoxUrl { get; set; }

    /// <summary>
    /// Release notes / changelog for the new version.
    /// </summary>
    public string ReleaseNotes { get; set; } = string.Empty;
}
