using ClaudeCode.Core.ViewModels;
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Turns a 0–100 percent into a StrokeDashArray that draws that fraction of a circle.
/// ConverterParameter is "radius,thickness" (device-independent units), default "6,2".</summary>
public sealed class PercentToDashArrayConverter : IValueConverter
{
    /// <summary>
    /// Converts an integer percentage into a dash-and-gap DoubleCollection that renders a circular progress stroke, using the parsed radius and thickness from the converter parameter and drawing nothing for non-integer or unresolved values.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>The object result.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (radius, thickness) = CircularProgressGeometry.ParseSpec(parameter as string);

        // Both bindings supply an int (ChatViewModel.ContextUsagePercent, UsageLimitDisplay.Percent);
        // anything else - including the unresolved-binding sentinel - draws nothing. Kept identical
        // to the sibling PercentToStarWidthConverter so the two can never disagree.
        int percent = value is int i ? i : 0;
        var (dash, gap) = CircularProgressGeometry.ComputeDash(percent, radius, thickness);
        return new DoubleCollection { dash, gap };
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
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
