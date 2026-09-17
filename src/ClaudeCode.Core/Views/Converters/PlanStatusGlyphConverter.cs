using ClaudeCode.Contracts;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

public sealed class PlanStatusGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        PlanEntryStatus.Completed => "\u2611", // ☑
        PlanEntryStatus.InProgress => "\u25D0", // ◐
        _ => "\u2610", // ☐
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
