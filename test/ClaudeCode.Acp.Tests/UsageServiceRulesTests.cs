using ClaudeCode.Contracts;
using System;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>Covers <see cref="UsageServiceRules"/>, the dependency-free decisions of the VSIX usage
/// service.</summary>
public sealed class UsageServiceRulesTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 5, 4, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void IsWithinBurstWindow_ASnapshotFetchedTenSecondsAgoIsStale() => Assert.False(UsageServiceRules.IsWithinBurstWindow(_now - TimeSpan.FromSeconds(10), _now));

    [Fact]
    public void IsWithinBurstWindow_ASnapshotFetchedJustBeforeTheWindowElapsedIsReused() => Assert.True(UsageServiceRules.IsWithinBurstWindow(_now - UsageServiceRules.RefetchBurstWindow + TimeSpan.FromMilliseconds(1), _now));

    [Fact]
    public void IsWithinBurstWindow_ASnapshotExactlyAsOldAsTheWindowIsStale() => Assert.False(UsageServiceRules.IsWithinBurstWindow(_now - UsageServiceRules.RefetchBurstWindow, _now));

    [Fact]
    public void IsWithinBurstWindow_ASnapshotFromTheFutureIsStale() => Assert.False(UsageServiceRules.IsWithinBurstWindow(_now + TimeSpan.FromMinutes(1), _now));

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    [InlineData(100, 100)]
    [InlineData(0, 0)]
    [InlineData(47, 47)]
    public void ClampPercent_KeepsTheValueInsideZeroToOneHundred(int reported, int expected) => Assert.Equal(expected, UsageServiceRules.ClampPercent(reported));
}
