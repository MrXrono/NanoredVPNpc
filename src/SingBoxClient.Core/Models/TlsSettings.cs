namespace SingBoxClient.Core.Models;

/// <summary>
/// TLS configuration for a server connection.
/// </summary>
public class TlsSettings
{
    /// <summary>
    /// SNI server name sent during TLS handshake.
    /// </summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>
    /// uTLS fingerprint (e.g. "chrome", "firefox", "safari").
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// ALPN negotiation protocols (e.g. ["h2", "http/1.1"]).
    /// </summary>
    public List<string> Alpn { get; set; } = new();

    /// <summary>
    /// Whether to skip TLS certificate verification.
    /// </summary>
    public bool AllowInsecure { get; set; } = false;

    /// <summary>
    /// REALITY public key (base64). Non-empty enables REALITY TLS.
    /// </summary>
    public string RealityPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// REALITY short ID (hex string).
    /// </summary>
    public string RealityShortId { get; set; } = string.Empty;

    /// <summary>
    /// Whether REALITY is configured (derived from RealityPublicKey presence).
    /// </summary>
    public bool IsReality => !string.IsNullOrEmpty(RealityPublicKey);
}
