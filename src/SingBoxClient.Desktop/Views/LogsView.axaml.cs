using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using SingBoxClient.Desktop.ViewModels;

namespace SingBoxClient.Desktop.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is LogsViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsViewModel.LogText)
            && DataContext is LogsViewModel { AutoScroll: true })
        {
            Dispatcher.UIThread.Post(() =>
            {
                var textBox = this.FindControl<TextBox>("LogTextBox");
                if (textBox is not null)
                {
                    textBox.CaretIndex = textBox.Text?.Length ?? 0;
                }
            }, DispatcherPriority.Background);
        }
    }
}
