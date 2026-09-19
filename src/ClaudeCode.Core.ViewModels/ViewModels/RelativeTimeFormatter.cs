using System;
using System.Globalization;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Pure relative-time labelling for session timestamps ("2h ago", "yesterday", or a short date once
/// the entry is a week old). Kept here (XAML-free, with the clock passed in) so the bucket boundaries
/// are directly unit-testable; <c>ClaudeCode.Core.Views.Converters.RelativeTimeConverter</c> is a thin
/// binding shim that supplies <see cref="DateTimeOffset.Now"/> and the binding's culture.
/// </summary>
public static class RelativeTimeFormatter
{
    /// <summary>
    /// Describes <paramref name="timestamp"/> relative to <paramref name="now"/>. A timestamp in the
    /// future (clock skew, or an agent-reported time) is clamped to "now" rather than rendered as a
    /// negative age. Every branch formats with <paramref name="culture"/> so one label never mixes
    /// the binding culture with the ambient one.
    /// </summary>
    public static string Describe(DateTimeOffset now, DateTimeOffset timestamp, CultureInfo? culture)
    {
        CultureInfo format = culture ?? CultureInfo.CurrentCulture;

        TimeSpan age = now - timestamp;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;

        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return ((int)age.TotalMinutes).ToString(format) + "m ago";
        if (age < TimeSpan.FromHours(24)) return ((int)age.TotalHours).ToString(format) + "h ago";
        if (age < TimeSpan.FromHours(48)) return "yesterday";
        if (age < TimeSpan.FromDays(7)) return ((int)age.TotalDays).ToString(format) + "d ago";

        return timestamp.ToLocalTime().ToString("MMM d", format);
    }
}
