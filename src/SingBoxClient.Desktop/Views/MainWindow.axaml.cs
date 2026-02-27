using Avalonia.Controls;
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
