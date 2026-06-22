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
            // Toon de volledige waarde (tot 15 decimalen, zonder onnodige nullen).
            // Een vaste, korte notatie zoals "0.###" rondt af op 3 decimalen, waardoor
            // bv. een DBC-schaal van 0,00390625 als "0,004" verschijnt en bij het
            // opnieuw opslaan zou verminken. Dit behoudt de precisie bij het bewerken.
            return d.ToString("0.###############", CultureInfo.CurrentCulture);
        }

        return value.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text)
        {
            return DependencyProperty.UnsetValue;
        }

        var input = text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            // Leeg veld: een nullable doel (bv. optioneel tijdfilter) wordt gewist (null);
            // een niet-nullable doel behoudt zijn huidige waarde.
            return Nullable.GetUnderlyingType(targetType) is not null
                ? null
                : DependencyProperty.UnsetValue;
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
