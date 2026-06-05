using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CanAnalyzer.App.Infrastructure;

/// <summary>
/// WPF converter that accepts both comma and dot as decimal separator.
/// </summary>
public sealed class FlexibleDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is double d)
        {
            return d.ToString("0.###", CultureInfo.CurrentCulture);
        }

        return value.ToString() ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text)
        {
            return DependencyProperty.UnsetValue;
        }

        var input = text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return DependencyProperty.UnsetValue;
        }

        if (double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            return parsed;
        }

        var swapped = input.Contains(',')
            ? input.Replace(',', '.')
            : input.Replace('.', ',');
        if (double.TryParse(swapped, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return parsed;
        }

        return DependencyProperty.UnsetValue;
    }
}
