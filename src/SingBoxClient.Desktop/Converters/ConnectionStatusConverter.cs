namespace SingBoxClient.Desktop.Converters;

using Avalonia.Data.Converters;
using Avalonia.Media;
using SingBoxClient.Core.Models;
using System;
using System.Globalization;

/// <summary>
/// Converts ConnectionStatus enum to a localized display string.
/// </summary>
public class ConnectionStatusToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ConnectionStatus status)
            return "Unknown";

        return status switch
        {
            ConnectionStatus.Disconnected => "Disconnected",
            ConnectionStatus.Connecting   => "Connecting...",
            ConnectionStatus.Connected    => "Connected",
            ConnectionStatus.Disconnecting => "Disconnecting...",
            ConnectionStatus.Error        => "Connection Error",
            _ => "Unknown"
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
