using ClaudeCode.Core.ViewModels;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Renders a <see cref="DateTimeOffset"/> as a short relative-time label ("2h ago",
/// "yesterday", or a short date once it is far enough in the past). The bucket logic lives in
/// <see cref="RelativeTimeFormatter"/> so it is unit-testable; this shim only supplies the clock.</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    /// <summary>
    /// Converts a DateTimeOffset value into a human-readable relative time description, such as &quot;5 minutes ago&quot;, based on the current time and the specified culture, returning an empty string for non-DateTimeOffset values.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>The object result.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTimeOffset timestamp
            ? RelativeTimeFormatter.Describe(DateTimeOffset.Now, timestamp, culture)
            : string.Empty;

    /// <summary>
    /// Throws a NotSupportedException because the converter supports only one-way conversion from the source value to the target type.
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
