using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Renders a <see cref="DateTimeOffset"/> as a short relative-time label ("2h ago",
/// "yesterday", or a short date once it is far enough in the past).</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset timestamp)
        {
            return string.Empty;
        }

        TimeSpan age = DateTimeOffset.Now - timestamp;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;

        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromHours(24)) return $"{(int)age.TotalHours}h ago";
        if (age < TimeSpan.FromHours(48)) return "yesterday";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays}d ago";

        return timestamp.ToLocalTime().ToString("MMM d", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
