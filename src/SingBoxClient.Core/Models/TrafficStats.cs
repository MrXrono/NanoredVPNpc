namespace SingBoxClient.Core.Models;

/// <summary>
/// Real-time traffic statistics from the sing-box core.
/// </summary>
public class TrafficStats
{
    /// <summary>
    /// Current upload speed in bytes per second.
    /// </summary>
    public long UploadSpeed { get; set; }

    /// <summary>
    /// Current download speed in bytes per second.
    /// </summary>
    public long DownloadSpeed { get; set; }

    /// <summary>
    /// Total bytes uploaded during this session.
    /// </summary>
    public long TotalUpload { get; set; }

    /// <summary>
    /// Total bytes downloaded during this session.
    /// </summary>
    public long TotalDownload { get; set; }
}
