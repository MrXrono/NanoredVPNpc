using System.Text.RegularExpressions;

namespace SingBoxClient.Core.Helpers;

/// <summary>
/// Maps ISO 3166-1 alpha-2 country codes to display names and flag emoji.
/// </summary>
public static partial class CountryCodeHelper
{
    // ── Country name dictionaries ────────────────────────────────────────

    private static readonly Dictionary<string, string> NamesEn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DE"] = "Germany",
        ["RU"] = "Russia",
        ["US"] = "United States",
        ["NL"] = "Netherlands",
        ["FI"] = "Finland",
        ["GB"] = "United Kingdom",
        ["JP"] = "Japan",
        ["SG"] = "Singapore",
        ["FR"] = "France",
        ["CA"] = "Canada",
        ["AU"] = "Australia",
        ["KR"] = "South Korea",
        ["IN"] = "India",
        ["BR"] = "Brazil",
        ["TR"] = "Turkey",
        ["UA"] = "Ukraine",
        ["KZ"] = "Kazakhstan",
        ["PL"] = "Poland",
        ["SE"] = "Sweden",
        ["CH"] = "Switzerland",
        ["HK"] = "Hong Kong",
        ["TW"] = "Taiwan",
    };

    private static readonly Dictionary<string, string> NamesRu = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DE"] = "\u0413\u0435\u0440\u043c\u0430\u043d\u0438\u044f",
        ["RU"] = "\u0420\u043e\u0441\u0441\u0438\u044f",
        ["US"] = "\u0421\u0428\u0410",
        ["NL"] = "\u041d\u0438\u0434\u0435\u0440\u043b\u0430\u043d\u0434\u044b",
        ["FI"] = "\u0424\u0438\u043d\u043b\u044f\u043d\u0434\u0438\u044f",
        ["GB"] = "\u0412\u0435\u043b\u0438\u043a\u043e\u0431\u0440\u0438\u0442\u0430\u043d\u0438\u044f",
        ["JP"] = "\u042f\u043f\u043e\u043d\u0438\u044f",
        ["SG"] = "\u0421\u0438\u043d\u0433\u0430\u043f\u0443\u0440",
        ["FR"] = "\u0424\u0440\u0430\u043d\u0446\u0438\u044f",
        ["CA"] = "\u041a\u0430\u043d\u0430\u0434\u0430",
        ["AU"] = "\u0410\u0432\u0441\u0442\u0440\u0430\u043b\u0438\u044f",
        ["KR"] = "\u042e\u0436\u043d\u0430\u044f \u041a\u043e\u0440\u0435\u044f",
        ["IN"] = "\u0418\u043d\u0434\u0438\u044f",
        ["BR"] = "\u0411\u0440\u0430\u0437\u0438\u043b\u0438\u044f",
        ["TR"] = "\u0422\u0443\u0440\u0446\u0438\u044f",
        ["UA"] = "\u0423\u043a\u0440\u0430\u0438\u043d\u0430",
        ["KZ"] = "\u041a\u0430\u0437\u0430\u0445\u0441\u0442\u0430\u043d",
        ["PL"] = "\u041f\u043e\u043b\u044c\u0448\u0430",
        ["SE"] = "\u0428\u0432\u0435\u0446\u0438\u044f",
        ["CH"] = "\u0428\u0432\u0435\u0439\u0446\u0430\u0440\u0438\u044f",
        ["HK"] = "\u0413\u043e\u043d\u043a\u043e\u043d\u0433",
        ["TW"] = "\u0422\u0430\u0439\u0432\u0430\u043d\u044c",
    };

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Get the human-readable country name for an ISO 3166-1 alpha-2 code.
    /// </summary>
    /// <param name="code">Two-letter country code (e.g. "DE").</param>
    /// <param name="language">Language: "en" (default) or "ru".</param>
    /// <returns>Country name or the original code if not found.</returns>
    public static string GetCountryName(string code, string language = "en")
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var dict = language.Equals("ru", StringComparison.OrdinalIgnoreCase) ? NamesRu : NamesEn;
        return dict.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();
    }

    /// <summary>
    /// Convert a two-letter country code to its flag emoji using Unicode regional indicator symbols.
    /// </summary>
    /// <example>GetFlag("DE") returns the German flag emoji.</example>
    public static string GetFlag(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length < 2)
            return string.Empty;

        var upper = code.ToUpperInvariant();

        // Regional Indicator Symbol Letter A = U+1F1E6.
        // Offset from 'A' (U+0041) to Regional Indicator 'A'.
        const int offset = 0x1F1E6 - 'A';

        return string.Concat(
            char.ConvertFromUtf32(upper[0] + offset),
            char.ConvertFromUtf32(upper[1] + offset));
    }

    /// <summary>
    /// Try to extract a 2-3 letter country code prefix from a server name
    /// (e.g. "DE-Frankfurt-01" yields "DE").
    /// </summary>
    /// <returns>Uppercase country code or <c>null</c> if no match.</returns>
    public static string? ExtractCountryCode(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            return null;

        var match = CountryPrefixRegex().Match(serverName);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Combines flag emoji and country name for UI display.
    /// </summary>
    /// <example>GetDisplayName("DE", "en") returns flag-emoji + " Germany".</example>
    public static string GetDisplayName(string code, string language = "en")
    {
        var flag = GetFlag(code);
        var name = GetCountryName(code, language);
        return string.IsNullOrEmpty(flag) ? name : $"{flag} {name}";
    }

    [GeneratedRegex(@"^([a-zA-Z]{2,3})[-_\s]", RegexOptions.Compiled)]
    private static partial Regex CountryPrefixRegex();
}
