using System.Net.Security;
using System.Runtime.InteropServices;
using SingBoxClient.Core.Constants;

namespace SingBoxClient.Core.Helpers;

/// <summary>
/// Factory methods for pre-configured <see cref="HttpClient"/> instances.
/// </summary>
public static class HttpClientFactory
{
    /// <summary>
    /// Create a basic <see cref="HttpClient"/> with the default User-Agent header.
    /// </summary>
    public static HttpClient CreateDefault()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppDefaults.UserAgent);
        return client;
    }

    /// <summary>
    /// Create an <see cref="HttpClient"/> that accepts any server certificate
    /// (useful for self-signed or internal APIs). Timeout is set to 30 seconds.
    /// </summary>
    public static HttpClient CreateIgnoreCert()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                static (_, _, _, _) => true,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppDefaults.UserAgent);
        return client;
    }

    /// <summary>
    /// Create an <see cref="HttpClient"/> configured for API calls:
    /// certificate validation disabled, base address set,
    /// User-Agent, X-Client-Version, and X-Client-Arch headers attached.
    /// </summary>
    /// <param name="baseUrl">API base URL (e.g. "https://api.example.com").</param>
    public static HttpClient CreateApiClient(string baseUrl)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                static (_, _, _, _) => true,
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppDefaults.UserAgent);
        client.DefaultRequestHeaders.Add("X-Client-Version", AppDefaults.Version);
        client.DefaultRequestHeaders.Add("X-Client-Arch", RuntimeInformation.OSArchitecture.ToString());

        return client;
    }
}
