using System;
using System.Globalization;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Relative-time labels for session timestamps ("2h ago", "yesterday", or a short date once
/// the entry is a week old).
/// </summary>
public static class RelativeTimeFormatter
{
    /// <summary>
    /// Describes <paramref name="timestamp"/> relative to <paramref name="now"/>, formatted with
    /// <paramref name="culture"/> (or the current culture when null). A future timestamp is treated as "now".
    /// </summary>
    public static string Describe(DateTimeOffset now, DateTimeOffset timestamp, CultureInfo? culture)
    {
        CultureInfo format = culture ?? CultureInfo.CurrentCulture;

        TimeSpan age = now - timestamp;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return ((int)age.TotalMinutes).ToString(format) + "m ago";
        }

        if (age < TimeSpan.FromHours(24))
        {
            return ((int)age.TotalHours).ToString(format) + "h ago";
        }

        if (age < TimeSpan.FromHours(48))
        {
            return "yesterday";
        }

        if (age < TimeSpan.FromDays(7))
        {
            return ((int)age.TotalDays).ToString(format) + "d ago";
        }

        return timestamp.ToLocalTime().ToString("MMM d", format);
    }
}
