namespace SingBoxClient.Core.Models;

/// <summary>
/// Represents the current VPN connection state.
/// </summary>
public enum ConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Error = 4,
    Disconnecting = 5
}
