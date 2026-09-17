using System;
using System.Globalization;
using System.Windows.Data;
using ClaudeCode.Contracts;

namespace ClaudeCode.Core.Views.Converters
{
    /// <summary>Maps a <see cref="PlanEntryStatus"/> to a small text glyph for the plan strip.</summary>
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
}
