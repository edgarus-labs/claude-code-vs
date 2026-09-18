using ClaudeCode.Contracts;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Maps the raw <see cref="ToolCallStatus"/> enum to a human-readable label for display.</summary>
public sealed class ToolCallStatusToDisplayTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ToolCallStatus.Pending => "Pending",
        ToolCallStatus.InProgress => "In progress",
        ToolCallStatus.Completed => "Completed",
        ToolCallStatus.Failed => "Failed",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
