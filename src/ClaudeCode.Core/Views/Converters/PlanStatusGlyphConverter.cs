using ClaudeCode.Contracts;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>
/// Provides a value converter that maps plan status values to their corresponding display glyphs for WPF bindings.
/// </summary>
public sealed class PlanStatusGlyphConverter : IValueConverter
{
    /// <summary>
    /// Converts a PlanEntryStatus value into a checkbox-style Unicode symbol for display, returning a checked box for completed entries, a half-filled circle for in-progress entries, and an empty box for all other statuses.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>The object result.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        PlanEntryStatus.Completed => "\u2611", // ☑
        PlanEntryStatus.InProgress => "\u25D0", // ◐
        _ => "\u2610", // ☐
    };

    /// <summary>
    /// Converts a value back from the target representation to its source type, which is not supported by this one-way converter and always throws a NotSupportedException.
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
