using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace SingBoxClient.Desktop;

/// <summary>
/// Provides localized strings via an indexer, allowing XAML bindings like:
///   Text="{Binding [Settings], Source={x:Static local:LocalizationManager.Instance}}"
/// Raises PropertyChanged("Item[]") when the culture changes, refreshing all bound strings.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private readonly ResourceManager _rm;
    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    private LocalizationManager()
    {
        _rm = new ResourceManager(
            "SingBoxClient.Desktop.Localization.Strings",
            typeof(LocalizationManager).Assembly);
    }

    /// <summary>
    /// Get a localized string by key. Returns the key itself if not found.
    /// </summary>
    public string this[string key] => _rm.GetString(key, _culture) ?? key;

    /// <summary>
    /// Switch culture and notify all XAML bindings to refresh.
    /// </summary>
    public void SetCulture(CultureInfo culture)
    {
        _culture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
