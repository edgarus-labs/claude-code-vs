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

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
