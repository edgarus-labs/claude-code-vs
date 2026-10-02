using System;
using System.Globalization;

namespace ClaudeCode.Contracts;

/// <summary>Normalizes the reset timestamp of a <see cref="UsageLimit"/> as it comes out of a JSON
/// reader.</summary>
public static class UsageResetTimestamp
{
    /// <summary>Converts the value a JSON reader produced for a reset timestamp (a
    /// <see cref="DateTimeOffset"/>, a <see cref="DateTime"/>, or a string) into a
    /// <see cref="DateTimeOffset"/>. Returns null for a missing value, a non-temporal value, an
    /// unparseable string, and for a local time whose UTC equivalent falls outside
    /// <see cref="DateTimeOffset"/>'s range.</summary>
    public static DateTimeOffset? FromJsonValue(object? value)
    {
        switch (value)
        {
            case DateTimeOffset offset:
                return offset;
            case DateTime dateTime:
                try
                {
                    return new DateTimeOffset(dateTime);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            case string text:
                return DateTimeOffset.TryParse(
                    text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
                    ? parsed
                    : (DateTimeOffset?)null;
            default:
                return null;
        }
    }
}
