using ClaudeCode.Core.ViewModels;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Renders a <see cref="DateTimeOffset"/> as a short relative-time label ("2h ago",
/// "yesterday", or a short date) using <see cref="RelativeTimeFormatter"/>.</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTimeOffset timestamp
            ? RelativeTimeFormatter.Describe(DateTimeOffset.Now, timestamp, culture)
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
