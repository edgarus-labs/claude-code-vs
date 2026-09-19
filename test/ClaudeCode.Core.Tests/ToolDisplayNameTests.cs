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

    // Shell titles are executable input: the adapter sets a Bash/PowerShell title to the whole
    // command and this client, which advertises no terminal capability, gets nothing else to show.
    // Collapsing to one line or a short cap would hide the tail of what the user is approving, so
    // every line survives into the permission card, whose ScrollViewer (MaxHeight 120) bounds the
    // layout instead (ChatPanelView.xaml).
    [Fact]
    public void MultiLineShellCommandReachesThePermissionCardIntact()
    {
        const string command = "git add -A\ngit commit -m \"first line\" && rm -rf build/";

        var permission = new PermissionRequestViewModel(ToolDisplayName.Describe(command), [], _ => { });

        Assert.Equal(command, permission.Title);
    }

    [Theory]
    // CRLF and a blank leading/trailing line are wrapper noise, not command text.
    [InlineData("  \r\n\r\n  Read report.txt\r\nrm -rf /\r\n", "Read report.txt\nrm -rf /")]
    // A trailing tab still separates words rather than gluing them together.
    [InlineData("echo\tfoo", "echo foo")]
    public void LineStructureIsPreservedAndTidied(string title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));

    // The remaining bound is a denial-of-service cap far above any real command, not a layout rule.
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

    // A three-segment identifier: the tool segment keeps its own separators rather than being
    // re-split, so the label still names the tool the routing key names.
    [Fact]
    public void McpToolSegmentContainingTheSeparatorIsKeptWhole() =>
        Assert.Equal("A: B c", ToolDisplayName.Describe("mcp__a__b__c"));

    // Control and bidi formatting characters are removed: U+202E reverses the rest of the displayed
    // name, which on a consent surface can make a write look like a read. Zero-width format
    // characters go the same way - invisible, and able to split a word the user is reading.
    [Theory]
    [InlineData("\u202ERead \u0007secret.txt", "Read secret.txt")]
    [InlineData("\uFEFFRead \u200Bsecret.txt", "Read secret.txt")]
    // Neutralizing before parsing also stops a leading control character from hiding the MCP marker.
    [InlineData("\u202Emcp__visual-studio__listAppWindows", "Visual Studio: List app windows")]
    // U+2028 is a line break to WPF, so it must survive as one: dropping it would fuse two commands
    // into "rm -rf /echo ok".
    [InlineData("rm -rf /\u2028echo ok", "rm -rf /\u2028echo ok")]
    public void ControlAndBidiCharactersAreNeutralized(string title, string expected) =>
        Assert.Equal(expected, ToolDisplayName.Describe(title));
}
