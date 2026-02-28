using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace SingBoxClient.Desktop.Converters;

/// <summary>
/// Returns true if the bound value equals the ConverterParameter (string comparison, case-insensitive).
/// Used for sidebar active page accent bar visibility.
/// </summary>
public class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string current
            && parameter is string target
            && string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns a brush from application resources based on whether the bound string matches ConverterParameter.
/// Looks up ActiveBrushKey when matched, InactiveBrushKey otherwise.
/// Supports theme switching because resources are resolved on every Convert call.
/// </summary>
public class ActivePageBrushConverter : IValueConverter
{
    public string ActiveBrushKey { get; set; } = "AccentLightBrush";
    public string InactiveBrushKey { get; set; } = "TextSecondaryBrush";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isActive = value is string current
            && parameter is string target
            && string.Equals(current, target, StringComparison.OrdinalIgnoreCase);

        var key = isActive ? ActiveBrushKey : InactiveBrushKey;

        if (Application.Current?.TryFindResource(key, out var resource) == true && resource is IBrush brush)
            return brush;

        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
