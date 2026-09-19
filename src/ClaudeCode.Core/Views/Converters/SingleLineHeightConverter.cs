using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Height of one text line for the given [FontSize, FontFamily], plus the vertical padding
/// named by ConverterParameter (default 0). Lets a control docked to the bottom of a growing text
/// box stay centered on the text while it is still a single line, at any font size.</summary>
public sealed class SingleLineHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double fontSize || values[1] is not FontFamily family)
        {
            return double.NaN; // let the layout fall back to auto-sizing
        }

        double padding = 0;
        if (parameter is string spec) double.TryParse(spec, NumberStyles.Float, CultureInfo.InvariantCulture, out padding);
        return Math.Ceiling(family.LineSpacing * fontSize + padding);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
