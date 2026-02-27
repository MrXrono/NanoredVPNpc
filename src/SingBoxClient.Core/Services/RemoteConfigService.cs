using Serilog;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Fetches and caches remote routing rules from the backend.
/// </summary>
public interface IRemoteConfigService
{
    /// <summary>
    /// Fetch the latest remote routing rules from the backend API.
    /// Caches the result for offline access.
    /// </summary>
    Task<List<RoutingRule>?> FetchAsync();

    /// <summary>
    /// Return the last successfully fetched remote rules from cache.
    /// Returns an empty list if no rules have been fetched yet.
    /// </summary>
    List<RoutingRule> GetCachedRules();

    /// <summary>
    /// Alias for <see cref="FetchAsync"/>. Used by Desktop ViewModels.
    /// </summary>
    Task<List<RoutingRule>?> FetchRoutingRulesAsync() => FetchAsync();

    /// <summary>
    /// Fetch announcements from the backend. Used by Desktop ViewModels.
    /// </summary>
    Task<List<Announcement>> FetchAnnouncementsAsync();
}

/// <summary>
/// Default implementation with in-memory caching of the last fetched result.
/// </summary>
public class RemoteConfigService : IRemoteConfigService
{
    private readonly ILogger _logger = Log.ForContext<RemoteConfigService>();
    private readonly IApiClient _apiClient;

    private List<RoutingRule> _cachedRules = new();

    public RemoteConfigService(IApiClient apiClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    // ── Fetch ────────────────────────────────────────────────────────────────

    public async Task<List<RoutingRule>?> FetchAsync()
    {
        try
        {
            var rules = await _apiClient.GetRemoteConfigAsync();

            if (rules is null)
            {
                _logger.Warning("Remote config returned null — keeping cached rules");
                return null;
            }

            _cachedRules = rules;
            _logger.Information("Fetched {Count} remote routing rules", rules.Count);
            return rules;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch remote config");
            return null;
        }
    }

    // ── Alias: FetchRoutingRulesAsync ───────────────────────────────────────

    /// <inheritdoc />
    public Task<List<RoutingRule>?> FetchRoutingRulesAsync() => FetchAsync();

    // ── Fetch Announcements ─────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<List<Announcement>> FetchAnnouncementsAsync()
    {
        try
        {
            var announcements = await _apiClient.GetAnnouncementsAsync(null);
            _logger.Information("Fetched {Count} announcements via RemoteConfigService", announcements.Count);
            return announcements;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch announcements");
            return new List<Announcement>();
        }
    }

    // ── Cache ────────────────────────────────────────────────────────────────

    public List<RoutingRule> GetCachedRules()
    {
        return new List<RoutingRule>(_cachedRules);
    }
}
