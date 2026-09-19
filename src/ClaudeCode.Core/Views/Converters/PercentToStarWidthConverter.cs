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
        // Both bindings supply an int (ChatViewModel.ContextUsagePercent, UsageLimitDisplay.Percent);
        // anything else - including the unresolved-binding sentinel - renders an empty bar. Kept
        // identical to the sibling PercentToDashArrayConverter so the two can never disagree.
        double percent = value is int i ? i : 0;
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
