namespace SingBoxClient.Core.Models;

/// <summary>
/// VPN operating mode.
/// Proxy — system-level SOCKS/HTTP proxy via sing-box.
/// TUN — virtual network adapter capturing all traffic.
/// </summary>
public enum ConnectionMode
{
    Proxy = 0,
    TUN = 1
}
