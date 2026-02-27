using System.Text.Json.Nodes;

namespace SingBoxClient.Core.Config;

/// <summary>
/// Builds the sing-box experimental section.
/// Configures the Clash-compatible REST API and persistent cache.
/// </summary>
public static class ExperimentalConfig
{
    /// <summary>
    /// Builds the experimental configuration with Clash API and cache file settings.
    /// </summary>
    /// <param name="clashApiPort">
    /// Port for the Clash-compatible REST API (default 9090).
    /// The API is used for real-time traffic statistics, connection management,
    /// and health checking via <c>ClashApiClient</c>.
    /// </param>
    /// <returns>JsonObject representing the experimental section of sing-box config.</returns>
    public static JsonObject Build(int clashApiPort = 9090)
    {
        return new JsonObject
        {
            ["clash_api"] = new JsonObject
            {
                ["external_controller"] = $"127.0.0.1:{clashApiPort}",
                ["secret"] = ""
            },
            ["cache_file"] = new JsonObject
            {
                ["enabled"] = true,
                ["path"] = "cache.db"
            }
        };
    }
}
