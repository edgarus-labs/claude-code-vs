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
    /// <summary>
    /// Converts a font size and font family into a line height by multiplying the family&apos;s line spacing by the font size and adding an optional padding value, returning NaN to defer to automatic sizing when inputs are invalid.
    /// </summary>
    /// <param name="values">The collection of values.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>The object result.</returns>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2 || values[0] is not double fontSize || values[1] is not FontFamily family)
        {
            return double.NaN; // let the layout fall back to auto-sizing
        }

        double padding = 0;
        if (parameter is string spec) double.TryParse(spec, NumberStyles.Float, CultureInfo.InvariantCulture, out padding);
        return Math.Ceiling(family.LineSpacing * fontSize + padding);
    }

    /// <summary>
    /// Converts a value back to multiple source types, which is not supported and always throws a NotSupportedException.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="targetTypes">The collection of target types.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>A collection of object items.</returns>
    /// <exception cref="NotSupportedException">Thrown when an error occurs during execution.</exception>
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
