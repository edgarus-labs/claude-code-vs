using ClaudeCode.Core.ViewModels;
using System;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class SessionTitleFormatTests
{
    [Theory]
    [InlineData("first\nsecond")]
    [InlineData("first\rsecond")]
    [InlineData("first\r\nsecond")]
    [InlineData("first\u0085second")]
    [InlineData("first\u2028second")]
    [InlineData("first\u2029second")]
    public void Describe_KeepsOnlyTheFirstLine_ForEveryCharacterWpfBreaksOn(string title) =>
        Assert.Equal("first", SessionTitleFormat.Describe(title, null));

    [Fact]
    public void Describe_EllipsisesAnOverlongTitleAtTheCap()
    {
        var described = SessionTitleFormat.Describe(new string('x', 500), null);

        Assert.Equal(SessionTitleFormat.MaxTitleLength, described.Length);
        Assert.Equal(new string('x', 79) + "…", described);
    }

    [Fact]
    public void Describe_CuttingAnOverlongTitle_NeverLeavesHalfOfASurrogatePair()
    {
        var title = new string('a', 78) + "\U0001F600" + new string('b', 40);

        var described = SessionTitleFormat.Describe(title, null);

        Assert.Equal(new string('a', 78) + "…", described);
    }

    [Fact]
    public void Describe_StripsControlAndBidiCharactersThatTrimmingLeavesBehind()
    {
        var described = SessionTitleFormat.Describe("Fix the\u0007 login\u202E bug", null);

        Assert.Equal("Fix the login bug", described);
    }

    [Fact]
    public void Describe_WhenALineIsNothingButControlCharacters_MovesOnToTheNextOne()
    {
        Assert.Equal("real title", SessionTitleFormat.Describe("\u202E\u200F\n real title ", null));
    }

    [Fact]
    public void Describe_WithNoUsableTitle_FallsBackToTheSessionIdPrefix()
    {
        Assert.Equal("a1b2c3d4", SessionTitleFormat.Describe(null, "a1b2c3d4-e5f6-7890"));
        Assert.Equal("a1b2c3d4", SessionTitleFormat.Describe("  \n \r ", "a1b2c3d4-e5f6-7890"));
        Assert.Equal("short", SessionTitleFormat.Describe(null, "short"));
        Assert.Equal(string.Empty, SessionTitleFormat.Describe(null, null));
    }

    [Fact]
    public void Describe_SessionIdFallback_IsStrippedAndKeptToOneLineLikeTheTitle()
    {
        Assert.Equal("a1b2", SessionTitleFormat.Describe(null, "a1b2\nc3d4e5f6-7890"));
        Assert.Equal("a1b2c3d4", SessionTitleFormat.Describe(null, "\u202Ea1b2\u0007c3d4e5f6-7890"));
        Assert.Equal(string.Empty, SessionTitleFormat.Describe(null, "\u202E\n\r"));
    }

    [Theory]
    [InlineData("\u200B\u200C\u200D")]
    [InlineData("\u2060\uFEFF")]
    public void Describe_TitleOfOnlyZeroWidthCharacters_FallsBackToTheSessionIdPrefix(string title) =>
        Assert.Equal("a1b2c3d4", SessionTitleFormat.Describe(title, "a1b2c3d4-e5f6"));

    [Fact]
    public void Describe_StripsZeroWidthCharactersInsideATitle() =>
        Assert.Equal("Fix login", SessionTitleFormat.Describe("Fix\u200B login\uFEFF", null));

    [Fact]
    public void SingleLine_AppliesTheCallersOwnCap()
    {
        Assert.Equal(new string('y', 159) + "…", SessionTitleFormat.SingleLine(new string('y', 400), 160));
        Assert.Equal("only this", SessionTitleFormat.SingleLine("only this\rand not this", 160));
    }
}
