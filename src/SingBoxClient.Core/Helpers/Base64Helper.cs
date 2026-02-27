using System.Text;
using System.Text.RegularExpressions;

namespace SingBoxClient.Core.Helpers;

/// <summary>
/// Utilities for decoding standard and URL-safe Base64 strings.
/// </summary>
public static partial class Base64Helper
{
    /// <summary>
    /// Decode a Base64 string (standard or URL-safe) to a UTF-8 string.
    /// Automatically adds missing padding characters.
    /// </summary>
    /// <param name="input">Base64-encoded input.</param>
    /// <returns>Decoded UTF-8 string, or empty string on failure.</returns>
    public static string Decode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        try
        {
            // Convert URL-safe characters to standard Base64
            var base64 = input
                .Replace('-', '+')
                .Replace('_', '/');

            // Add missing padding
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "=";  break;
            }

            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Check whether <paramref name="input"/> looks like a valid Base64 string
    /// (standard or URL-safe, with optional padding).
    /// </summary>
    public static bool IsBase64(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        // Must contain only valid Base64 / URL-safe characters and optional padding
        return Base64Regex().IsMatch(input);
    }

    [GeneratedRegex(@"^[A-Za-z0-9+/\-_]+={0,2}$", RegexOptions.Compiled)]
    private static partial Regex Base64Regex();
}
