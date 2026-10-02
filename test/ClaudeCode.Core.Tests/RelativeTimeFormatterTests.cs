using ClaudeCode.Core.ViewModels;
using System;
using System.Globalization;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class RelativeTimeFormatterTests
{
    private static readonly DateTimeOffset _now = new(new DateTime(2024, 3, 14, 12, 0, 0, DateTimeKind.Local));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(3599, "59m ago")]
    [InlineData(3600, "1h ago")]
    [InlineData(86_399, "23h ago")]
    [InlineData(86_400, "yesterday")]
    [InlineData(172_799, "yesterday")]
    [InlineData(172_800, "2d ago")]
    [InlineData(604_799, "6d ago")]
    public void Describe_BucketBoundaries(int ageSeconds, string expected)
    {
        DateTimeOffset timestamp = _now.AddSeconds(-ageSeconds);

        Assert.Equal(expected, RelativeTimeFormatter.Describe(_now, timestamp, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Describe_SevenDaysOrOlder_FallsBackToAShortDate()
    {
        DateTimeOffset timestamp = _now.AddDays(-7);

        Assert.Equal("Mar 7", RelativeTimeFormatter.Describe(_now, timestamp, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Describe_FutureTimestamp_ClampsToNowInsteadOfRenderingANegativeAge()
    {
        DateTimeOffset timestamp = _now.AddHours(5);

        Assert.Equal("just now", RelativeTimeFormatter.Describe(_now, timestamp, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Describe_DateFallback_UsesTheSuppliedCultureNotAHardCodedOne()
    {
        DateTimeOffset timestamp = new(new DateTime(2024, 1, 5, 12, 0, 0, DateTimeKind.Local));
        DateTimeOffset now = timestamp.AddDays(30);

        string english = RelativeTimeFormatter.Describe(now, timestamp, new CultureInfo("en-US"));
        string french = RelativeTimeFormatter.Describe(now, timestamp, new CultureInfo("fr-FR"));

        Assert.Equal("Jan 5", english);
        Assert.NotEqual(english, french);
    }

    [Fact]
    public void Describe_NullCulture_FallsBackToTheCurrentCultureNotToAHardCodedOne()
    {
        DateTimeOffset timestamp = _now.AddDays(-30);
        var pinned = new CultureInfo("fr-FR");
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = pinned;
        try
        {
            string fallback = RelativeTimeFormatter.Describe(_now, timestamp, culture: null);

            Assert.Equal(RelativeTimeFormatter.Describe(_now, timestamp, pinned), fallback);
            Assert.NotEqual(RelativeTimeFormatter.Describe(_now, timestamp, CultureInfo.InvariantCulture), fallback);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
