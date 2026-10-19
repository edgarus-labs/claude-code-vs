using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>
/// Provides a WPF value converter that maps null values to Visibility.Collapsed and non-null values to Visibility.Visible, with ConvertBack left unsupported.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Converts the input value to a Visibility enumeration value, optionally inverting the null check based on the parameter.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>The object result.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNotNull = value is not null;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            isNotNull = !isNotNull;
        }

        return isNotNull ? Visibility.Visible : Visibility.Collapsed;
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
