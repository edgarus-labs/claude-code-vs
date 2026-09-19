using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// The decision logic behind the VS control channel's build and Output-window methods, extracted
/// from the EnvDTE glue because both fail silently when wrong: an unmatched Error List row makes a
/// failed build report zero errors, and a wrong tail slice returns the wrong part of a build log
/// while still claiming to be the end of it.
/// </summary>
public sealed class VsBuildChannelRulesTests
{
    [Fact]
    public void MatchesProject_BareUniqueName_MatchesTheRequestedProject()
    {
        Assert.True(VsBuildChannelRules.MatchesProject("ClaudeCode.Core", "claudecode.core"));
    }

    [Fact]
    public void MatchesProject_SolutionRelativeProjectPath_MatchesTheRequestedProject()
    {
        Assert.True(VsBuildChannelRules.MatchesProject(@"src\ClaudeCode.Core\ClaudeCode.Core.csproj", "ClaudeCode.Core"));
    }

    [Fact]
    public void MatchesProject_DifferentProject_DoesNotMatch()
    {
        Assert.False(VsBuildChannelRules.MatchesProject(@"src\ClaudeCode.Acp\ClaudeCode.Acp.csproj", "ClaudeCode.Core"));
    }

    [Fact]
    public void MatchesProject_DottedBareName_IsNotTreatedAsAProjectFileName()
    {
        // A bare unique name routinely contains dots. Stripping the last segment as if it were a
        // file extension attributes the test project's errors to the project under build, and
        // reports its own failure against a request for a parent-prefixed name.
        Assert.False(VsBuildChannelRules.MatchesProject("ClaudeCode.Core.Tests", "ClaudeCode.Core"));
        Assert.False(VsBuildChannelRules.MatchesProject("ClaudeCode.Core", "ClaudeCode"));
    }

    [Fact]
    public void MatchesProject_ForwardSlashProjectPath_MatchesTheRequestedProject()
    {
        // The net8.0 test host may run where Path treats only '/' as a separator; the rule has to
        // resolve both forms the same way it does inside devenv.
        Assert.True(VsBuildChannelRules.MatchesProject("src/ClaudeCode.Core/ClaudeCode.Core.csproj", "ClaudeCode.Core"));
        Assert.True(VsBuildChannelRules.MatchesProject(@"native\Engine\Engine.vcxproj", "Engine"));
    }

    [Fact]
    public void MatchesProject_ItemWithNoProject_DoesNotMatch()
    {
        // Solution-level and IntelliSense-only rows carry no project; counting them against the
        // requested project would attribute another project's failure to this build.
        Assert.False(VsBuildChannelRules.MatchesProject(null, "ClaudeCode.Core"));
        Assert.False(VsBuildChannelRules.MatchesProject(string.Empty, "ClaudeCode.Core"));
    }

    [Fact]
    public void ClampOutputChars_OmittedRequest_UsesTheDefault()
    {
        Assert.Equal(20_000, VsBuildChannelRules.ClampOutputChars(null, 20_000, 200_000));
    }

    [Fact]
    public void ClampOutputChars_HonoursTheCeiling()
    {
        Assert.Equal(200_000, VsBuildChannelRules.ClampOutputChars(int.MaxValue, 20_000, 200_000));
    }

    [Fact]
    public void ClampOutputChars_NeverYieldsAZeroLengthWindow()
    {
        Assert.Equal(1, VsBuildChannelRules.ClampOutputChars(0, 20_000, 200_000));
        Assert.Equal(1, VsBuildChannelRules.ClampOutputChars(-5, 20_000, 200_000));
    }

    [Fact]
    public void ClampOutputChars_LegalRequestPassesThrough()
    {
        Assert.Equal(4_096, VsBuildChannelRules.ClampOutputChars(4_096, 20_000, 200_000));
    }

    [Fact]
    public void TakeOutputTail_ShortEnoughTextIsReturnedWhole()
    {
        Assert.Equal("build succeeded", VsBuildChannelRules.TakeOutputTail("build succeeded", 20));
        Assert.Equal("exact", VsBuildChannelRules.TakeOutputTail("exact", 5));
    }

    [Fact]
    public void TakeOutputTail_KeepsTheEndOfTheLogNotTheStart()
    {
        // The interesting part of a build log is its tail; a head slice would answer with the
        // banner while claiming to be the last maxChars characters.
        Assert.Equal("error CS1002", VsBuildChannelRules.TakeOutputTail("warning CS0168\nerror CS1002", 12));
    }

    [Fact]
    public void TakeOutputTail_EmptyPaneIsEmptyNotNull()
    {
        Assert.Equal(string.Empty, VsBuildChannelRules.TakeOutputTail(null, 20));
        Assert.Equal(string.Empty, VsBuildChannelRules.TakeOutputTail(string.Empty, 20));
    }
}
