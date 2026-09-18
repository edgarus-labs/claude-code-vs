using ClaudeCode.Contracts;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Renders a <see cref="SessionSummary"/>'s display title, falling back to a truncated
/// session id when the agent did not record a title.</summary>
public sealed class SessionTitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SessionSummary session)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(session.Title))
        {
            return session.Title!;
        }

        string id = session.SessionId;
        return id.Length <= 8 ? id : id.Substring(0, 8);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
