using Avalonia.Controls;
using SingBoxClient.Desktop.ViewModels;

namespace SingBoxClient.Desktop.Views;

public partial class AnnouncementsWindow : Window
{
    public AnnouncementsWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is AnnouncementsViewModel vm)
        {
            vm.CloseAction = () => Close();
        }
    }
}
