using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Renders a <see cref="SessionSummary"/>'s display title for a history row. The title is
/// agent-reported, so it goes through <see cref="SessionTitleFormat"/> - the same rule the panel
/// header uses - which collapses it to its first non-empty line, caps its length, and falls back to
/// a truncated session id when the agent did not record a title.</summary>
public sealed class SessionTitleConverter : IValueConverter
{
    /// <summary>
    /// Converts a SessionSummary value into a formatted display title using its title and session identifier, returning an empty string for any other input.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>The object result.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SessionSummary session
            ? SessionTitleFormat.Describe(session.Title, session.SessionId)
            : string.Empty;

    /// <summary>
    /// Converts a value back to its source type, which is not supported by this one-way converter and always throws a NotSupportedException.
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
