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
    public void LimitBlockquoteNesting_AtMaxDepth_ReturnsUnchanged()
    {
        var markdown = new string('>', MarkdownSafetyLimits.MaxBlockquoteDepth) + " text";

        var result = MarkdownSafetyLimits.LimitBlockquoteNesting(markdown);

        Assert.Same(markdown, result);
    }

    [Fact]
    public void LimitBlockquoteNesting_OneOverMaxDepth_TruncatesToExactlyMaxDepth()
    {
        var markdown = new string('>', MarkdownSafetyLimits.MaxBlockquoteDepth + 1) + " text";

        var result = MarkdownSafetyLimits.LimitBlockquoteNesting(markdown);

        Assert.Equal(MarkdownSafetyLimits.MaxBlockquoteDepth, CountBlockquoteDepth(result));
        Assert.EndsWith("text", result, StringComparison.Ordinal);
    }

    // Mirrors the marker-counting rule under test: each level is '>' optionally followed by one
    // space, exactly as LimitBlockquoteNesting itself parses and re-emits markers.
    private static int CountBlockquoteDepth(string line)
    {
        var depth = 0;
        var position = 0;
        while (position < line.Length && line[position] == '>')
        {
            depth++;
            position++;
            if (position < line.Length && line[position] == ' ')
            {
                position++;
            }
        }

        return depth;
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

    [Theory]
    [InlineData(0, 100)]
    [InlineData(7999, 100)]
    [InlineData(8000, 100)]
    [InlineData(80000, 1000)]
    [InlineData(1_000_000, 1000)]
    public void ComputeRenderInterval_ScalesWithTextLengthUpToCap(int textLength, int expectedMilliseconds)
    {
        var interval = MarkdownSafetyLimits.ComputeRenderInterval(textLength);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), interval);
    }

    [Fact]
    public void EnsureBlankLineBeforeFences_FenceImmediatelyAfterText_InsertsBlankLine()
    {
        var markdown = "Here is the output:\n```console\nsome output\n```";

        var result = MarkdownSafetyLimits.EnsureBlankLineBeforeFences(markdown);

        Assert.Equal("Here is the output:\n\n```console\nsome output\n```", result);
    }

    [Fact]
    public void EnsureBlankLineBeforeFences_FenceAlreadyPrecededByBlankLine_LeavesUnchanged()
    {
        var markdown = "Here is the output:\n\n```console\nsome output\n```";

        var result = MarkdownSafetyLimits.EnsureBlankLineBeforeFences(markdown);

        Assert.Equal(markdown, result);
    }

    [Fact]
    public void EnsureBlankLineBeforeFences_FenceAtStartOfDocument_LeavesUnchanged()
    {
        var markdown = "```console\nsome output\n```\nAfter.";

        var result = MarkdownSafetyLimits.EnsureBlankLineBeforeFences(markdown);

        Assert.Equal(markdown, result);
    }

    [Fact]
    public void EnsureBlankLineBeforeFences_ContentInsideFenceLookingLikeTextIsUntouched()
    {
        // A closing fence right after code content (not text) must not get a spurious blank line -
        // only an *opening* fence directly after non-blank text does.
        var markdown = "```console\nline one\nline two\n```\nAfter.";

        var result = MarkdownSafetyLimits.EnsureBlankLineBeforeFences(markdown);

        Assert.Equal(markdown, result);
    }

    [Fact]
    public void EnsureBlankLineBeforeFences_NoFence_ReturnsSameInstance()
    {
        var markdown = "Just plain text, no code blocks here.";

        var result = MarkdownSafetyLimits.EnsureBlankLineBeforeFences(markdown);

        Assert.Same(markdown, result);
    }
}
