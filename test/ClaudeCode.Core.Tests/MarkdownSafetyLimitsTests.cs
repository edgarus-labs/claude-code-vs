using ClaudeCode.Core.ViewModels;
using System;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class MarkdownSafetyLimitsTests
{
    [Fact]
    public void LimitMarkdownLength_AtMaxLength_ReturnsUnchanged()
    {
        var markdown = new string('a', MarkdownSafetyLimits.MaxMarkdownLength);

        var result = MarkdownSafetyLimits.LimitMarkdownLength(markdown);

        Assert.Same(markdown, result);
    }

    [Fact]
    public void LimitMarkdownLength_OneOverMaxLength_TruncatesWithMarker()
    {
        var markdown = new string('a', MarkdownSafetyLimits.MaxMarkdownLength + 1);

        var result = MarkdownSafetyLimits.LimitMarkdownLength(markdown);

        Assert.StartsWith(new string('a', MarkdownSafetyLimits.MaxMarkdownLength), result, StringComparison.Ordinal);
        Assert.Contains("truncated", result, StringComparison.Ordinal);
        Assert.True(result.Length > MarkdownSafetyLimits.MaxMarkdownLength);
    }

    [Fact]
    public void IsNavigableLink_AcceptsOrdinaryHttpsHost()
    {
        Assert.True(MarkdownSafetyLimits.IsNavigableLink(new Uri("https://example.com/path")));
    }

    [Fact]
    public void IsNavigableLink_RejectsLoopbackHost()
    {
        Assert.False(MarkdownSafetyLimits.IsNavigableLink(new Uri("http://127.0.0.1/")));
    }

    [Fact]
    public void IsNavigableLink_RejectsAllZeroesHost()
    {
        // Uri.IsLoopback does not classify 0.0.0.0 as loopback, but it routes to the local host on
        // most platforms (Windows in particular) and must be rejected the same way 127.0.0.1 is.
        Assert.False(MarkdownSafetyLimits.IsNavigableLink(new Uri("http://0.0.0.0/")));
    }

    [Theory]
    [InlineData("http://[::]/")]
    [InlineData("https://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:127.42.0.7]/")]
    [InlineData("http://[::ffff:0.0.0.0]/")]
    public void IsNavigableLink_RejectsLocalIpv6Destinations(string target)
    {
        Assert.False(MarkdownSafetyLimits.IsNavigableLink(new Uri(target)));
    }

    [Theory]
    [InlineData("https://[2001:4860:4860::8888]/")]
    [InlineData("https://[::ffff:8.8.8.8]/")]
    public void IsNavigableLink_AcceptsRemoteIpDestinations(string target)
    {
        Assert.True(MarkdownSafetyLimits.IsNavigableLink(new Uri(target)));
    }

    [Fact]
    public void IsNavigableLink_NullUri_ReturnsFalse()
    {
        Assert.False(MarkdownSafetyLimits.IsNavigableLink(null!));
    }

    [Theory]
    [InlineData("docs/page.md")]
    [InlineData("/etc/passwd")]
    public void IsNavigableLink_RelativeUri_ReturnsFalse(string target)
    {
        Assert.False(MarkdownSafetyLimits.IsNavigableLink(new Uri(target, UriKind.Relative)));
    }
}
