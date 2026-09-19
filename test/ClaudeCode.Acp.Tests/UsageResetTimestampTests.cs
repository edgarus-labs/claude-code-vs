using System;
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
    public void FromJsonValue_ParsesAStringTimestampInvariantly()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 5, 4, 12, 30, 0, TimeSpan.Zero),
            UsageResetTimestamp.FromJsonValue("2026-05-04T12:30:00Z"));
    }

    [Fact]
    public void FromJsonValue_ReturnsNullForALocalTimeWhoseUtcEquivalentIsOutOfRange()
    {
        TimeSpan localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        if (localOffset == TimeSpan.Zero)
        {
            // No local DateTime can leave DateTimeOffset's range in UTC itself.
            return;
        }

        DateTime outOfRange = DateTime.SpecifyKind(
            localOffset > TimeSpan.Zero ? DateTime.MinValue : DateTime.MaxValue, DateTimeKind.Local);

        Assert.Null(UsageResetTimestamp.FromJsonValue(outOfRange));
    }

    [Fact]
    public void FromJsonValue_ReturnsNullForAMissingOrNonTemporalValue()
    {
        Assert.Null(UsageResetTimestamp.FromJsonValue(null));
        Assert.Null(UsageResetTimestamp.FromJsonValue(1_767_000_000L));
        Assert.Null(UsageResetTimestamp.FromJsonValue("not a timestamp"));
    }
}
