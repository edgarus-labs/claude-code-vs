using ClaudeCode.Contracts;
using System;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// Covers the decision logic behind the VS control channel's build and Output-window methods:
/// project matching, output length clamping, log tail slicing and well-known Output pane lookup.
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
        Assert.False(VsBuildChannelRules.MatchesProject("ClaudeCode.Core.Tests", "ClaudeCode.Core"));
        Assert.False(VsBuildChannelRules.MatchesProject("ClaudeCode.Core", "ClaudeCode"));
    }

    [Fact]
    public void MatchesProject_ForwardSlashProjectPath_MatchesTheRequestedProject()
    {
        Assert.True(VsBuildChannelRules.MatchesProject("src/ClaudeCode.Core/ClaudeCode.Core.csproj", "ClaudeCode.Core"));
        Assert.True(VsBuildChannelRules.MatchesProject(@"native\Engine\Engine.vcxproj", "Engine"));
    }

    [Fact]
    public void MatchesProject_ItemWithNoProject_DoesNotMatch()
    {
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
        Assert.Equal("error CS1002", VsBuildChannelRules.TakeOutputTail("warning CS0168\nerror CS1002", 12));
    }

    [Fact]
    public void TakeOutputTail_EmptyPaneIsEmptyNotNull()
    {
        Assert.Equal(string.Empty, VsBuildChannelRules.TakeOutputTail(null, 20));
        Assert.Equal(string.Empty, VsBuildChannelRules.TakeOutputTail(string.Empty, 20));
    }

    [Fact]
    public void WellKnownOutputPaneGuid_TheThreeDocumentedAliases_ResolveRegardlessOfTheUiLanguage()
    {
        Assert.Equal(new Guid("1BD8A850-02D1-11D1-BEE7-00A0C913D1F8"), VsBuildChannelRules.WellKnownOutputPaneGuid("Build"));
        Assert.Equal(new Guid("FC076020-078A-11D1-A7DF-00A0C9110051"), VsBuildChannelRules.WellKnownOutputPaneGuid("debug"));
        Assert.Equal(new Guid("3C24D581-5591-4884-A571-9FE89915CD64"), VsBuildChannelRules.WellKnownOutputPaneGuid("GENERAL"));
    }

    [Fact]
    public void WellKnownOutputPaneGuid_AnyOtherPane_IsMatchedByNameOnly()
    {
        Assert.Null(VsBuildChannelRules.WellKnownOutputPaneGuid("Git"));
        Assert.Null(VsBuildChannelRules.WellKnownOutputPaneGuid("Debugowanie"));
    }
}
