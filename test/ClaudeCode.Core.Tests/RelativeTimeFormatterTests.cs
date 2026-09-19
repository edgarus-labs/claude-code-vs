using ClaudeCode.Core.ViewModels;
using System;
using System.Globalization;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class RelativeTimeFormatterTests
{
    private static readonly DateTimeOffset Now = new(new DateTime(2024, 3, 14, 12, 0, 0, DateTimeKind.Local));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]            // last second of the "just now" bucket
    [InlineData(60, "1m ago")]              // minute boundary
    [InlineData(3599, "59m ago")]           // last second of the minute bucket
    [InlineData(3600, "1h ago")]            // hour boundary
    [InlineData(86_399, "23h ago")]         // last second of the hour bucket
    [InlineData(86_400, "yesterday")]       // 24h boundary
    [InlineData(172_799, "yesterday")]      // last second of "yesterday"
    [InlineData(172_800, "2d ago")]         // 48h boundary
    [InlineData(604_799, "6d ago")]         // last second before the date fallback
    public void Describe_BucketBoundaries(int ageSeconds, string expected)
    {
        DateTimeOffset timestamp = Now.AddSeconds(-ageSeconds);

        Assert.Equal(expected, RelativeTimeFormatter.Describe(Now, timestamp, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Describe_SevenDaysOrOlder_FallsBackToAShortDate()
    {
        DateTimeOffset timestamp = Now.AddDays(-7);

        Assert.Equal("Mar 7", RelativeTimeFormatter.Describe(Now, timestamp, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Describe_FutureTimestamp_ClampsToNowInsteadOfRenderingANegativeAge()
    {
        DateTimeOffset timestamp = Now.AddHours(5);

        Assert.Equal("just now", RelativeTimeFormatter.Describe(Now, timestamp, CultureInfo.InvariantCulture));
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
        // Pinned to a culture whose "MMM d" differs from the invariant one, so the assertion cannot
        // be satisfied by an implementation that quietly formats with CultureInfo.InvariantCulture.
        DateTimeOffset timestamp = Now.AddDays(-30);
        var pinned = new CultureInfo("fr-FR");
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = pinned;
        try
        {
            string fallback = RelativeTimeFormatter.Describe(Now, timestamp, culture: null);

            Assert.Equal(RelativeTimeFormatter.Describe(Now, timestamp, pinned), fallback);
            Assert.NotEqual(RelativeTimeFormatter.Describe(Now, timestamp, CultureInfo.InvariantCulture), fallback);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
