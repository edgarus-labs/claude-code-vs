using System;
using System.Globalization;
using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>Covers <see cref="UsageResetTimestamp.FromJsonValue(object?)"/>, which normalizes the
/// value a JSON reader produced for a limit's reset timestamp. A JSON reader materializes a
/// well-formed ISO-8601 timestamp as a <see cref="DateTime"/>/<see cref="DateTimeOffset"/> rather
/// than a string, so a string-only conversion silently shows no reset time at all.</summary>
public sealed class UsageResetTimestampTests
{
    [Fact]
    public void FromJsonValue_ConvertsTheDateTimeAJsonReaderProducesForAnIsoTimestamp()
    {
        var utc = new DateTime(2026, 5, 4, 12, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTimeOffset(2026, 5, 4, 12, 30, 0, TimeSpan.Zero), UsageResetTimestamp.FromJsonValue(utc));
    }

    [Fact]
    public void FromJsonValue_PassesThroughADateTimeOffset()
    {
        var offset = new DateTimeOffset(2026, 5, 4, 14, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal(offset, UsageResetTimestamp.FromJsonValue(offset));
    }

    [Fact]
    public void FromJsonValue_ParsesTheIsoStringFormTheEndpointSends()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 5, 4, 12, 30, 0, TimeSpan.Zero),
            UsageResetTimestamp.FromJsonValue("2026-05-04T12:30:00Z"));
    }

    [Fact]
    public void FromJsonValue_ParsesANonIsoStringTimestampInvariantlyUnderAHostileCurrentCulture()
    {
        // Deliberately not the ISO form: DateTimeOffset.TryParse has a culture-insensitive ISO-8601
        // fast path, so an ISO string parses identically under every culture and cannot show whether
        // the conversion pins a culture at all. A slash-separated form does - under th-TH's Buddhist
        // calendar this same text reads as year 1483 - so this is what makes the
        // CultureInfo.InvariantCulture pin load-bearing instead of decorative.
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try
        {
            Assert.Equal(
                new DateTimeOffset(2026, 5, 4, 12, 30, 0, TimeSpan.Zero),
                UsageResetTimestamp.FromJsonValue("2026/05/04 12:30:00 +00:00"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void FromJsonValue_ReturnsNullForAMissingOrNonTemporalValue()
    {
        Assert.Null(UsageResetTimestamp.FromJsonValue(null));
        Assert.Null(UsageResetTimestamp.FromJsonValue(1_767_000_000L));
        Assert.Null(UsageResetTimestamp.FromJsonValue("not a timestamp"));
    }
}
