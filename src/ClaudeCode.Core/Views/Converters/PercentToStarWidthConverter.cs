using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Converts a 0-100 percent value into a star-sized <see cref="GridLength"/>, for a
/// two-column Grid that renders a simple progress bar without a custom ProgressBar template.
/// With ConverterParameter="Invert", converts to the remaining (100 - percent) share instead.</summary>
public sealed class PercentToStarWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Same numeric acceptance as the sibling PercentToDashArrayConverter: a percent that arrives
        // as double/long must not silently render an empty bar.
        double percent = value switch
        {
            int i => i,
            // GridLength rejects NaN/Infinity outright, so a non-finite double becomes "empty bar".
            double d when !double.IsNaN(d) && !double.IsInfinity(d) => d,
            long l => l,
            _ => 0,
        };
        percent = Math.Max(0, Math.Min(100, percent));
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            percent = 100 - percent;
        }

        return new GridLength(percent, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
