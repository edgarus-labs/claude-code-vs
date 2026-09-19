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
        double radius = 6, thickness = 2;
        if (parameter is string spec)
        {
            var parts = spec.Split(',');
            if (parts.Length > 0) double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out radius);
            if (parts.Length > 1) double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out thickness);
        }

        double percent = value switch
        {
            int i => i,
            double d => d,
            long l => l,
            _ => 0,
        };
        percent = Math.Max(0, Math.Min(100, percent));
        // Dash lengths are in multiples of the stroke thickness.
        double circumference = 2 * Math.PI * radius / thickness;
        return new DoubleCollection { circumference * percent / 100, circumference + 1 };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
