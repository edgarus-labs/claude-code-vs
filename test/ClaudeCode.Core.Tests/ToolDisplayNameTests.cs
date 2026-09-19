using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ToolDisplayNameTests
{
    [Theory]
    [InlineData("mcp__visual-studio__listAppWindows", "Visual Studio: List app windows")]
    // Single-word server and tool: the only shape the hyphenated/camelCase row does not exercise.
    [InlineData("mcp__github__search", "Github: Search")]
    public void McpIdentifiersReadAsASentence(string title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));

    [Fact]
    public void AcronymsKeepTheirCase() =>
        Assert.Equal("Visual Studio: Open UI window", ToolDisplayName.Describe("mcp__visual-studio__openUIWindow"));

    // Tool titles the agent already writes for a human (built-ins, and titles carrying an argument)
    // must survive untouched - reformatting them would mangle paths, globs and quoting.
    [Theory]
    [InlineData("Read")]
    [InlineData("Find \"**/*.sln\"")]
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
    // A server segment built only from separators title-cases to an empty label. Showing the bare
    // tool label then presents an MCP call as a built-in - "mcp__.__editDocument" would reach the
    // transcript as "Edit document" and be rendered as a first-party file edit - so these join the
    // other malformed forms and keep their routing identifier.
    [InlineData("mcp__-__read", "mcp__-__read")]
    [InlineData("mcp__.__editDocument", "mcp__.__editDocument")]
    [InlineData("mcp__ __bash", "mcp__ __bash")]
    // Same in the other direction: a tool segment of only separators would leave a dangling "Foo: ".
    [InlineData("mcp__visual-studio__-", "mcp__visual-studio__-")]
    public void DegenerateInputDoesNotProduceAnEmptyOrMisleadingSubject(string? title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));

    // The permission card binds this string into a wrapping TextBlock that sits in an Auto grid row
    // above the Allow/Deny buttons. A title of a few thousand newlines wraps to as many rows, which
    // pushes those buttons and the composer out of the panel and leaves the user unable to answer or
    // cancel the prompt that is blocking the turn. Describe is the one normalization point every
    // surface shares, so it guarantees a single bounded row.
    [Theory]
    [InlineData("  \n\n  Read report.txt\nrm -rf /\n", "Read report.txt")]
    [InlineData("Read\rsecond line", "Read")]
    [InlineData("mcp__visual-studio__listAppWindows\nignored", "Visual Studio: List app windows")]
    public void TitleCollapsesToItsFirstNonEmptyLine(string title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));

    [Theory]
    [InlineData("Update ")]
    [InlineData("mcp__visual-studio__update")]
    public void OversizedTitlesAreCappedToOneRow(string prefix)
    {
        string described = ToolDisplayName.Describe(prefix + new string('x', 10_000));

        Assert.True(described.Length <= 160, $"expected at most 160 chars, got {described.Length}");
        Assert.EndsWith("…", described);
    }

    // Control and bidi formatting characters are removed: U+202E reverses the rest of the displayed
    // name, which on a consent surface can make a write look like a read.
    [Theory]
    [InlineData("\u202ERead \u0007secret.txt", "Read secret.txt")]
    // Neutralizing before parsing also stops a leading control character from hiding the MCP marker.
    [InlineData("\u202Emcp__visual-studio__listAppWindows", "Visual Studio: List app windows")]
    public void ControlAndBidiCharactersAreNeutralized(string title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));
}
