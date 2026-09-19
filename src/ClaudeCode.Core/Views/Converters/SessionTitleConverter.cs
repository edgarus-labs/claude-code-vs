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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SessionSummary session
            ? SessionTitleFormat.Describe(session.Title, session.SessionId)
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
