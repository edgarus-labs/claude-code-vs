using System;
using System.Globalization;
using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>Covers <see cref="UsageResetTimestamp.FromJsonValue(object?)"/>, which normalizes the
/// value a JSON reader produced for a limit's reset timestamp from a <see cref="DateTime"/>,
/// <see cref="DateTimeOffset"/> or string, and returns null for any other value.</summary>
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
