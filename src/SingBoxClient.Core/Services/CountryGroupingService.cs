using Serilog;
using SingBoxClient.Core.Helpers;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Groups server nodes by country for hierarchical UI display.
/// </summary>
public interface ICountryGroupingService
{
    /// <summary>
    /// Group a flat list of servers by their country code.
    /// </summary>
    /// <param name="servers">All available server nodes.</param>
    /// <param name="language">Language for display names ("en" or "ru").</param>
    /// <returns>Sorted list of country groups.</returns>
    List<CountryGroup> GroupServers(List<ServerNode> servers, string language = "en");

    /// <summary>
    /// Alias for <see cref="GroupServers"/> with default language.
    /// Used by Desktop ViewModels.
    /// </summary>
    List<CountryGroup> GroupByCountry(List<ServerNode> servers) => GroupServers(servers);
}

/// <summary>
/// Default implementation that extracts country codes from server names
/// and builds sorted <see cref="CountryGroup"/> entries.
/// </summary>
public class CountryGroupingService : ICountryGroupingService
{
    private readonly ILogger _logger = Log.ForContext<CountryGroupingService>();

    private const string UnknownCountryCode = "ZZ";
    private const string UnknownCountryNameEn = "Unknown";
    private const string UnknownCountryNameRu = "\u041d\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043d\u043e";

    // ── Public API ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public List<CountryGroup> GroupByCountry(List<ServerNode> servers) => GroupServers(servers);

    public List<CountryGroup> GroupServers(List<ServerNode> servers, string language = "en")
    {
        if (servers is null || servers.Count == 0)
            return new List<CountryGroup>();

        var groups = new Dictionary<string, CountryGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            var code = CountryCodeHelper.ExtractCountryCode(server.Name)
                       ?? UnknownCountryCode;

            if (!groups.TryGetValue(code, out var group))
            {
                group = new CountryGroup
                {
                    Code = code.ToUpperInvariant(),
                    DisplayName = code.Equals(UnknownCountryCode, StringComparison.OrdinalIgnoreCase)
                        ? GetUnknownDisplayName(language)
                        : CountryCodeHelper.GetDisplayName(code, language),
                    Servers = new List<ServerNode>(),
                };
                groups[code] = group;
            }

            group.Servers.Add(server);
        }

        // Sort groups alphabetically by country code, with "ZZ" (Unknown) at the end
        var result = groups.Values
            .OrderBy(g => g.Code == UnknownCountryCode ? 1 : 0)
            .ThenBy(g => g.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.Debug("Grouped {ServerCount} servers into {GroupCount} countries",
            servers.Count, result.Count);

        return result;
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static string GetUnknownDisplayName(string language)
    {
        var name = language.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? UnknownCountryNameRu
            : UnknownCountryNameEn;

        return $"{CountryCodeHelper.GetFlag(UnknownCountryCode)} {name}".Trim();
    }
}
