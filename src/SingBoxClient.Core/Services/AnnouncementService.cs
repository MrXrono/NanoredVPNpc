using Serilog;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Fetches and tracks server-side announcements.
/// </summary>
public interface IAnnouncementService
{
    /// <summary>
    /// Fetch the latest announcements from the backend. New announcements are marked unread.
    /// </summary>
    Task<List<Announcement>> FetchAsync();

    /// <summary>
    /// Whether there are any unread announcements.
    /// </summary>
    bool HasUnread { get; }

    /// <summary>
    /// Mark all cached announcements as read.
    /// </summary>
    void MarkAllRead();

    /// <summary>
    /// Return all cached announcements. Used by Desktop ViewModels.
    /// </summary>
    List<Announcement> GetAll();
}

/// <summary>
/// Default implementation with in-memory caching and read-status tracking.
/// </summary>
public class AnnouncementService : IAnnouncementService
{
    private readonly ILogger _logger = Log.ForContext<AnnouncementService>();
    private readonly IApiClient _apiClient;

    private List<Announcement> _cache = new();
    private readonly HashSet<string> _readIds = new();
    private DateTime? _lastFetchTime;

    public AnnouncementService(IApiClient apiClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    // ── Fetch ────────────────────────────────────────────────────────────────

    public async Task<List<Announcement>> FetchAsync()
    {
        try
        {
            var announcements = await _apiClient.GetAnnouncementsAsync(_lastFetchTime);
            _lastFetchTime = DateTime.UtcNow;

            if (announcements.Count == 0)
            {
                _logger.Debug("No new announcements");
                return _cache;
            }

            // Merge new announcements into cache, avoiding duplicates
            foreach (var ann in announcements)
            {
                var existing = _cache.FindIndex(a => a.Id == ann.Id);
                if (existing >= 0)
                {
                    // Update existing
                    ann.IsRead = _readIds.Contains(ann.Id);
                    _cache[existing] = ann;
                }
                else
                {
                    // New announcement — check if previously read
                    ann.IsRead = _readIds.Contains(ann.Id);
                    _cache.Add(ann);
                }
            }

            // Sort by date descending (newest first)
            _cache = _cache.OrderByDescending(a => a.CreatedAt).ToList();

            _logger.Information("Fetched {Count} announcements ({Unread} unread)",
                _cache.Count, _cache.Count(a => !a.IsRead));

            return _cache;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch announcements");
            return _cache;
        }
    }

    // ── GetAll ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public List<Announcement> GetAll()
    {
        return new List<Announcement>(_cache);
    }

    // ── Read tracking ────────────────────────────────────────────────────────

    public bool HasUnread => _cache.Any(a => !a.IsRead);

    public void MarkAllRead()
    {
        foreach (var ann in _cache)
        {
            ann.IsRead = true;
            _readIds.Add(ann.Id);
        }

        _logger.Debug("All {Count} announcements marked as read", _cache.Count);
    }
}
