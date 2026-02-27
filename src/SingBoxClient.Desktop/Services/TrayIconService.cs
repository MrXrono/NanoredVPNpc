namespace SingBoxClient.Desktop.Services;

using Avalonia.Controls;
using SingBoxClient.Core.Models;
using SingBoxClient.Core.Services;

/// <summary>
/// Manages the system tray icon, its context menu, and tooltip updates
/// based on VPN connection status.
/// </summary>
public class TrayIconService : IDisposable
{
    private TrayIcon? _trayIcon;
    private readonly ISettingsService _settings;

    public TrayIconService(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Creates and initializes the tray icon with a context menu bound to the main window.
    /// </summary>
    /// <param name="mainWindow">The application main window used for show/hide toggling.</param>
    public void Initialize(Window mainWindow)
    {
        // Create NativeMenu for tray context menu
        var menu = new NativeMenu();

        // Status item (disabled, just for display)
        var statusItem = new NativeMenuItem("Disconnected") { IsEnabled = false };
        menu.Items.Add(statusItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        // Connect/Disconnect toggle
        var toggleItem = new NativeMenuItem("Connect");
        menu.Items.Add(toggleItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        // Exit
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (s, e) =>
        {
            mainWindow.Close();
            Environment.Exit(0);
        };
        menu.Items.Add(exitItem);

        // Create tray icon
        _trayIcon = new TrayIcon
        {
            ToolTipText = "NanoredVPN",
            Menu = menu,
            IsVisible = true
        };

        // Click to show/hide window
        _trayIcon.Clicked += (s, e) =>
        {
            if (mainWindow.IsVisible)
                mainWindow.Hide();
            else
            {
                mainWindow.Show();
                mainWindow.Activate();
            }
        };
    }

    /// <summary>
    /// Updates the tray icon tooltip and the status menu item to reflect
    /// the current connection state.
    /// </summary>
    /// <param name="status">Current connection status.</param>
    /// <param name="country">Optional connected country name for display.</param>
    public void UpdateStatus(ConnectionStatus status, string? country = null)
    {
        if (_trayIcon == null) return;

        var statusText = status switch
        {
            ConnectionStatus.Connected => $"Connected: {country ?? ""}",
            ConnectionStatus.Connecting => "Connecting...",
            ConnectionStatus.Reconnecting => "Reconnecting...",
            ConnectionStatus.Error => "Connection Error",
            _ => "Disconnected"
        };

        _trayIcon.ToolTipText = $"NanoredVPN - {statusText}";

        // Update menu status item text
        if (_trayIcon.Menu?.Items.FirstOrDefault() is NativeMenuItem item)
            item.Header = statusText;
    }

    /// <summary>
    /// Updates the Connect/Disconnect toggle menu item text.
    /// </summary>
    /// <param name="isConnected">Whether the VPN is currently connected.</param>
    public void UpdateToggleText(bool isConnected)
    {
        if (_trayIcon?.Menu == null) return;

        // The toggle item is at index 2 (after status item and separator)
        if (_trayIcon.Menu.Items.Count > 2 && _trayIcon.Menu.Items[2] is NativeMenuItem toggleItem)
            toggleItem.Header = isConnected ? "Disconnect" : "Connect";
    }

    /// <summary>
    /// Disposes of the tray icon resources.
    /// </summary>
    public void Dispose()
    {
        _trayIcon?.Dispose();
    }
}
