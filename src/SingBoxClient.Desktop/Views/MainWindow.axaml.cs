using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using SingBoxClient.Core.Services;
using SingBoxClient.Desktop.Services;
using SingBoxClient.Desktop.ViewModels;

namespace SingBoxClient.Desktop.Views;

public partial class MainWindow : Window
{
    private ISettingsService? _settings;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => SubscribeToViewModelEvents();

        // Initialize tray icon after the window has loaded
        Loaded += OnWindowLoaded;

        // Enable window dragging from the custom title bar
        var titleBar = this.FindControl<Border>("TitleBar");
        if (titleBar is not null)
        {
            titleBar.PointerPressed += TitleBar_PointerPressed;
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // Double-click to toggle maximize/restore
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else
            {
                BeginMoveDrag(e);
            }
        }
    }

    private void OnWindowLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var trayService = App.Services?.GetService<TrayIconService>();
        trayService?.Initialize(this);

        // Restore saved window state
        _settings = App.Services?.GetService<ISettingsService>();
        RestoreWindowState();
    }

    private void RestoreWindowState()
    {
        if (_settings is null) return;

        var s = _settings.Current;

        // Restore size if saved
        if (s.WindowWidth > 0 && s.WindowHeight > 0)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }

        // Restore position if saved
        if (!double.IsNaN(s.WindowX) && !double.IsNaN(s.WindowY))
        {
            Position = new PixelPoint((int)s.WindowX, (int)s.WindowY);
        }

        // Restore maximized state
        if (s.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Save current window geometry to settings. Called from App shutdown.
    /// </summary>
    public void SaveWindowState()
    {
        if (_settings is null) return;

        var s = _settings.Current;

        s.WindowMaximized = WindowState == WindowState.Maximized;

        // Save size/position only when not maximized (to preserve the "normal" bounds)
        if (WindowState != WindowState.Maximized)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowX = Position.X;
            s.WindowY = Position.Y;
        }

        _settings.Save();
    }

    private void SubscribeToViewModelEvents()
    {
        if (DataContext is MainViewModel vm)
        {
            vm.OnMinimizeRequested += () => WindowState = WindowState.Minimized;
            vm.OnMaximizeRequested += () =>
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            vm.OnCloseRequested += () => Close();
        }
    }
}
