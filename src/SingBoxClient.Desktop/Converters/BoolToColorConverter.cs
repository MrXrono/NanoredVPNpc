namespace SingBoxClient.Desktop.Converters;

using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

public class BoolToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return new SolidColorBrush(Color.Parse("#00B894")); // green
        return new SolidColorBrush(Color.Parse("#555568")); // muted
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
