using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ToolDisplayNameTests
{
    [Theory]
    [InlineData("mcp__visual-studio__listAppWindows", "Visual Studio: List app windows")]
    [InlineData("mcp__visual-studio__buildSolution", "Visual Studio: Build solution")]
    [InlineData("mcp__visual-studio__evaluateExpression", "Visual Studio: Evaluate expression")]
    [InlineData("mcp__visual-studio__getWindowElements", "Visual Studio: Get window elements")]
    public void McpIdentifiersReadAsASentence(string title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));

    [Fact]
    public void AcronymsKeepTheirCase() =>
        Assert.Equal("Visual Studio: Open UI window", ToolDisplayName.Describe("mcp__visual-studio__openUIWindow"));

    // Tool titles the agent already writes for a human (built-ins, and titles carrying an argument)
    // must survive untouched - reformatting them would mangle paths, globs and quoting.
    [Theory]
    [InlineData("Read")]
    [InlineData("Bash")]
    [InlineData("Find \"**/*.sln\"")]
    [InlineData("Update Form1.cs")]
    public void HumanAuthoredTitlesArePassedThrough(string title) =>
        Assert.Equal(title, ToolDisplayName.Describe(title));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    // Malformed routing identifiers are still shown rather than blanked: a permission prompt with
    // no subject is worse than an ugly one.
    [InlineData("mcp__", "mcp__")]
    [InlineData("mcp__visual-studio__", "mcp__visual-studio__")]
    [InlineData("mcp__visual-studio", "mcp__visual-studio")]
    public void DegenerateInputDoesNotProduceAnEmptyOrMisleadingSubject(string? title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));
}
