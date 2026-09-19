using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>
/// Multiplies a length by the fraction in <c>ConverterParameter</c>, for bounding a panel against a
/// share of its host rather than a pixel constant. A fixed <c>MaxHeight</c> cannot serve both a
/// short tool window and a tall one: the same number that leaves a long prompt scrollable on a tall
/// panel makes a two-line prompt scroll needlessly on a short one.
/// <para>
/// Returns <see cref="double.PositiveInfinity"/> (i.e. no bound) for anything it cannot interpret,
/// including the zero-height first measure pass, because an unbounded panel degrades to the old
/// "grows and scrolls with its parent" behaviour while a zero bound would collapse it to nothing.
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

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
