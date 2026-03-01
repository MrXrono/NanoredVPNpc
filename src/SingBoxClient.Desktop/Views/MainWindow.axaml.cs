using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using SingBoxClient.Desktop.Services;
using SingBoxClient.Desktop.ViewModels;

namespace SingBoxClient.Desktop.Views;

public partial class MainWindow : Window
{
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
