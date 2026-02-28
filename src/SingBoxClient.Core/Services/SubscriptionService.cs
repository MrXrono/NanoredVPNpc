using System.Text.Json;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Helpers;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Fetches, parses, and caches subscription data (server lists) from a provider URL.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>
    /// Download a subscription URL, parse the server list, and cache it locally.
    /// </summary>
    /// <param name="url">Subscription URL.</param>
    /// <returns>Parsed list of server nodes.</returns>
    Task<List<ServerNode>> FetchAndParseAsync(string url);

    /// <summary>
    /// Extract subscription metadata from HTTP response headers.
    /// </summary>
    SubscriptionData? ParseHeaders(HttpResponseMessage response);
}

/// <summary>
/// Default implementation with base64/JSON parsing, local caching, and exponential backoff retry.
/// </summary>
public class SubscriptionService : ISubscriptionService
{
    private readonly ILogger _logger = Log.ForContext<SubscriptionService>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    // ── Fetch & Parse ────────────────────────────────────────────────────

    public async Task<List<ServerNode>> FetchAndParseAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Subscription URL must not be empty", nameof(url));

        _logger.Information("Fetching subscription from {Url}", url);

        using var http = HttpClientFactory.CreateIgnoreCert();
        http.DefaultRequestHeaders.UserAgent.Clear();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppDefaults.UserAgent);

        HttpResponseMessage response = await FetchWithRetryAsync(http, url);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        _logger.Debug("Subscription response length: {Length} chars", body.Length);

        var servers = ParseBody(body);
        _logger.Information("Parsed {Count} servers from subscription", servers.Count);

        // Cache servers locally
        await SaveCacheAsync(servers);

        return servers;
    }

    // ── Header Parsing ───────────────────────────────────────────────────

    public SubscriptionData? ParseHeaders(HttpResponseMessage response)
    {
        if (response is null)
            return null;

        var data = new SubscriptionData();

        // profile-update-interval (hours)
        if (response.Headers.TryGetValues("profile-update-interval", out var intervalValues))
        {
            var raw = intervalValues.FirstOrDefault();
            if (int.TryParse(raw, out var hours))
                data.UpdateInterval = hours;
        }

        // profile-title
        if (response.Headers.TryGetValues("profile-title", out var titleValues))
        {
            data.ProfileTitle = titleValues.FirstOrDefault() ?? string.Empty;
        }

        // subscription-userinfo: upload=N; download=N; total=N; expire=N
        if (response.Headers.TryGetValues("subscription-userinfo", out var userInfoValues))
        {
            var raw = userInfoValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(raw))
                ParseUserInfo(raw, data);
        }

        return data;
    }

    // ── Private: Retry Logic ─────────────────────────────────────────────

    private async Task<HttpResponseMessage> FetchWithRetryAsync(HttpClient http, string url)
    {
        var backoffMs = 1000; // 1s, 2s, 4s (exponential)

        for (var attempt = 1; attempt <= AppDefaults.SubscriptionRetries; attempt++)
        {
            try
            {
                var response = await http.GetAsync(url);
                return response;
            }
            catch (Exception ex) when (attempt < AppDefaults.SubscriptionRetries)
            {
                _logger.Warning(ex,
                    "Subscription fetch failed (attempt {Attempt}/{Max}), retrying in {Delay}ms",
                    attempt, AppDefaults.SubscriptionRetries, backoffMs);

                await Task.Delay(backoffMs);
                backoffMs *= 2; // Exponential backoff
            }
        }

        // Final attempt — let the exception propagate
        return await http.GetAsync(url);
    }

    // ── Private: Body Parsing ────────────────────────────────────────────

    private List<ServerNode> ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new List<ServerNode>();

        // Strategy 1: Try base64 decode → split by newlines → parse share links
        if (Base64Helper.IsBase64(body.Trim()))
        {
            var decoded = Base64Helper.Decode(body.Trim());
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                var shareLinks = ShareLinkParser.ParseMultiple(decoded);
                if (shareLinks.Count > 0)
                {
                    _logger.Debug("Parsed {Count} servers from base64 share links", shareLinks.Count);
                    return shareLinks;
                }
            }
        }

        // Strategy 2: Try parsing as raw share links (not base64 encoded)
        if (body.Contains("://"))
        {
            var shareLinks = ShareLinkParser.ParseMultiple(body);
            if (shareLinks.Count > 0)
            {
                _logger.Debug("Parsed {Count} servers from raw share links", shareLinks.Count);
                return shareLinks;
            }
        }

        // Strategy 3: Try JSON array of server objects
        try
        {
            var servers = JsonSerializer.Deserialize<List<ServerNode>>(body, JsonOptions);
            if (servers is not null && servers.Count > 0)
            {
                _logger.Debug("Parsed {Count} servers from JSON", servers.Count);
                return servers;
            }
        }
        catch (JsonException ex)
        {
            _logger.Debug(ex, "Body is not valid JSON array of servers");
        }

        _logger.Warning("Could not parse subscription body in any known format");
        return new List<ServerNode>();
    }

    // ── Private: User-Info Parsing ───────────────────────────────────────

    private static void ParseUserInfo(string raw, SubscriptionData data)
    {
        // Format: "upload=12345; download=67890; total=102400000; expire=1735689600"
        foreach (var part in raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx < 0) continue;

            var key = part[..eqIdx].Trim().ToLowerInvariant();
            var value = part[(eqIdx + 1)..].Trim();

            switch (key)
            {
                case "upload":
                    if (long.TryParse(value, out var upload))
                        data.UsedTraffic += upload;
                    break;

                case "download":
                    if (long.TryParse(value, out var download))
                        data.UsedTraffic += download;
                    break;

                case "total":
                    if (long.TryParse(value, out var total))
                        data.TotalTraffic = total;
                    break;

                case "expire":
                    if (long.TryParse(value, out var expire))
                        data.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expire).UtcDateTime;
                    break;
            }
        }
    }

    // ── Private: Cache ───────────────────────────────────────────────────

    private async Task SaveCacheAsync(List<ServerNode> servers)
    {
        try
        {
            var configDir = AppDefaults.ConfigDir;
            Directory.CreateDirectory(configDir);

            var cachePath = Path.Combine(configDir, AppDefaults.ServersFileName);
            var json = JsonSerializer.Serialize(servers, JsonOptions);
            await File.WriteAllTextAsync(cachePath, json);

            _logger.Debug("Cached {Count} servers to {Path}", servers.Count, cachePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to cache servers locally");
        }
    }
}
