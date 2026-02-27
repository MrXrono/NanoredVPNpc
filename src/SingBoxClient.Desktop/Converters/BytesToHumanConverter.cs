namespace SingBoxClient.Desktop.Converters;

using Avalonia.Data.Converters;
using System;
using System.Globalization;

/// <summary>
/// Converts byte count (long) to human-readable string (e.g. "1.2 MB", "345 KB").
/// </summary>
public class BytesToHumanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes)
            return "0 B";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:F1} {units[unitIndex]}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
