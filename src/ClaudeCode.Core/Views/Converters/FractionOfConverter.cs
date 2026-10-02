using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>
/// Multiplies a length by the fraction in <c>ConverterParameter</c>.
/// <para>
/// Returns <see cref="double.PositiveInfinity"/> for anything it cannot interpret, including a zero length.
/// </para>
/// </summary>
public sealed class FractionOfConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double available || double.IsNaN(available) || double.IsInfinity(available) || available <= 0)
        {
            return double.PositiveInfinity;
        }

        double fraction = parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => double.NaN,
        };

        return double.IsNaN(fraction) || fraction <= 0 ? double.PositiveInfinity : available * fraction;
    }

    /// <summary>
    /// Throws a NotSupportedException indicating that reverse conversion is not supported.
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
