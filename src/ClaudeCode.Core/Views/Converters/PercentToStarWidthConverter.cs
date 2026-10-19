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
    /// <summary>
    /// Converts an integer percentage, clamped to the range 0 to 100 and optionally inverted when the &quot;Invert&quot; parameter is supplied, into a star-sized GridLength for proportional grid column or row sizing.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>The object result.</returns>
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

    /// <summary>
    /// Throws a NotSupportedException to indicate that the conversion from the specified value to the target type is not supported.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>The object result.</returns>
    /// <exception cref="NotSupportedException">Thrown when an error occurs during execution.</exception>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
