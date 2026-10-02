using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ToolDisplayNameTests
{
    [Theory]
    [InlineData("mcp__visual-studio__listAppWindows", "Visual Studio: List app windows")]
    [InlineData("mcp__github__search", "Github: Search")]
    public void McpIdentifiersReadAsASentence(string title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));

    [Fact]
    public void AcronymsKeepTheirCase() =>
        Assert.Equal("Visual Studio: Open UI window", ToolDisplayName.Describe("mcp__visual-studio__openUIWindow"));

    [Theory]
    [InlineData("Read")]
    [InlineData("Find \"**/*.sln\"")]
    public void HumanAuthoredTitlesArePassedThrough(string title) =>
        Assert.Equal(title, ToolDisplayName.Describe(title));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("mcp__", "mcp__")]
    [InlineData("mcp__visual-studio__", "mcp__visual-studio__")]
    [InlineData("mcp__visual-studio", "mcp__visual-studio")]
    [InlineData("mcp__-__read", "mcp__-__read")]
    [InlineData("mcp__.__editDocument", "mcp__.__editDocument")]
    [InlineData("mcp__ __bash", "mcp__ __bash")]
    [InlineData("mcp__visual-studio__-", "mcp__visual-studio__-")]
    public void DegenerateInputDoesNotProduceAnEmptyOrMisleadingSubject(string? title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));

    [Fact]
    public void MultiLineShellCommandReachesThePermissionCardIntact()
    {
        const string command = "git add -A\ngit commit -m \"first line\" && rm -rf build/";

        var permission = new PermissionRequestViewModel(ToolDisplayName.Describe(command), [], _ => { });

        Assert.Equal(command, permission.Title);
    }

    [Theory]
    [InlineData("  \r\n\r\n  Read report.txt\r\nrm -rf /\r\n", "Read report.txt\nrm -rf /")]
    [InlineData("echo\tfoo", "echo foo")]
    public void LineStructureIsPreservedAndTidied(string title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));

    [Theory]
    [InlineData("Update ")]
    [InlineData("mcp__visual-studio__update")]
    public void OversizedTitlesAreCappedWithAnEllipsis(string prefix)
    {
        string described = ToolDisplayName.Describe(prefix + new string('x', 10_000));

        Assert.Equal(ToolDisplayName.MaxDisplayLength, described.Length);
        Assert.EndsWith("…", described);
    }

    [Fact]
    public void OversizedTitleCutNeverLeavesHalfOfASurrogatePair()
    {
        string title = new string('a', ToolDisplayName.MaxDisplayLength - 2) + "\U0001F600" + new string('b', 40);

        Assert.Equal(new string('a', ToolDisplayName.MaxDisplayLength - 2) + "…", ToolDisplayName.Describe(title));
    }

    [Fact]
    public void LongCommandBelowTheCapIsNotTruncated()
    {
        string command = "dotnet test " + new string('x', 300);

        Assert.Equal(command, ToolDisplayName.Describe(command));
    }

    [Fact]
    public void McpToolSegmentContainingTheSeparatorIsKeptWhole() =>
        Assert.Equal("A: B c", ToolDisplayName.Describe("mcp__a__b__c"));

    [Theory]
    [InlineData("\u202ERead \u0007secret.txt", "Read secret.txt")]
    [InlineData("\uFEFFRead \u200Bsecret.txt", "Read secret.txt")]
    [InlineData("\u202Emcp__visual-studio__listAppWindows", "Visual Studio: List app windows")]
    [InlineData("rm -rf /\u2028echo ok", "rm -rf /\u2028echo ok")]
    public void ControlAndBidiCharactersAreNeutralized(string title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));
}
