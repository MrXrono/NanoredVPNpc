namespace SingBoxClient.Desktop.Converters;

using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SingBoxClient.Core.Models;
using System;
using System.Globalization;

/// <summary>
/// Converts ConnectionStatus enum to a localized display string.
/// Reads from Application.Resources (DynamicResource-backed language dictionaries).
/// </summary>
public class ConnectionStatusToStringConverter : IValueConverter
{
    private static string L(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var val) == true && val is string s)
            return s;
        return key;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ConnectionStatus status)
            return L("Unknown");

        return status switch
        {
            ConnectionStatus.Disconnected => L("Disconnected"),
            ConnectionStatus.Connecting   => L("Connecting"),
            ConnectionStatus.Connected    => L("Connected"),
            ConnectionStatus.Disconnecting => L("Disconnecting"),
            ConnectionStatus.Error        => L("ConnectionError"),
            _ => L("Unknown")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts ConnectionStatus enum to a color brush for status indication.
/// </summary>
public class ConnectionStatusToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ConnectionStatus status)
            return new SolidColorBrush(Color.Parse("#555568"));

        return status switch
        {
            ConnectionStatus.Disconnected  => new SolidColorBrush(Color.Parse("#8888A0")),
            ConnectionStatus.Connecting    => new SolidColorBrush(Color.Parse("#FDCB6E")),
            ConnectionStatus.Connected     => new SolidColorBrush(Color.Parse("#00B894")),
            ConnectionStatus.Disconnecting => new SolidColorBrush(Color.Parse("#FDCB6E")),
            ConnectionStatus.Error         => new SolidColorBrush(Color.Parse("#E17055")),
            _ => new SolidColorBrush(Color.Parse("#555568"))
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
