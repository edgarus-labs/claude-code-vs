using ClaudeCode.Contracts;
using System;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// The decision logic behind the VS control channel's debugger methods, extracted from the EnvDTE
/// glue because every input here comes from an untrusted ACP agent request: a wrong breakpoint
/// match predicate destroys user state that no undo restores, and wrong frame arithmetic silently
/// attributes one frame's locals to another.
/// </summary>
public sealed class VsDebuggerChannelRulesTests
{
    private static readonly string[] _noArguments = Array.Empty<string>();

    [Fact]
    public void RequireRemovalArguments_LineWithoutPath_IsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => VsDebuggerChannelRules.RequireRemovalArguments(new[] { "line" }, null, 42));

        Assert.Contains("'line' requires 'path'", error.Message);
    }

    [Fact]
    public void RequireRemovalArguments_BlankPath_IsRejectedInsteadOfRemovingEveryBreakpoint()
    {
        var empty = Assert.Throws<InvalidOperationException>(
            () => VsDebuggerChannelRules.RequireRemovalArguments(new[] { "path" }, string.Empty, null));
        var whitespace = Assert.Throws<InvalidOperationException>(
            () => VsDebuggerChannelRules.RequireRemovalArguments(new[] { "path" }, "   ", null));

        Assert.Contains("omit it entirely", empty.Message);
        Assert.Contains("omit it entirely", whitespace.Message);
    }

    [Fact]
    public void RequireRemovalArguments_UnrecognisedMember_IsRejectedInsteadOfRemovingEveryBreakpoint()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => VsDebuggerChannelRules.RequireRemovalArguments(new[] { "filePath" }, null, null));

        Assert.Contains("filePath", error.Message);
    }

    [Fact]
    public void RequireRemovalArguments_TheDocumentedForms_AreAccepted()
    {
        // Omitting both members is the documented "remove every breakpoint" request and must keep
        // working: rejecting it would break the only way to clear the solution's breakpoints.
        VsDebuggerChannelRules.RequireRemovalArguments(_noArguments, null, null);
        VsDebuggerChannelRules.RequireRemovalArguments(new[] { "path" }, @"C:\repo\A.cs", null);
        VsDebuggerChannelRules.RequireRemovalArguments(new[] { "path", "line" }, @"C:\repo\A.cs", 42);
    }

    [Fact]
    public void MatchesBreakpointRemoval_LineWithoutPath_DoesNotMatchEveryBreakpoint()
    {
        Assert.Throws<InvalidOperationException>(
            () => VsDebuggerChannelRules.MatchesBreakpointRemoval(null, 42, @"C:\repo\Other.cs", 7));
    }

    [Fact]
    public void MatchesBreakpointRemoval_NoPathAndNoLine_MatchesEverything()
    {
        Assert.True(VsDebuggerChannelRules.MatchesBreakpointRemoval(null, null, @"C:\repo\A.cs", 7));
        Assert.True(VsDebuggerChannelRules.MatchesBreakpointRemoval(null, null, null, 0));
    }

    [Fact]
    public void MatchesBreakpointRemoval_PathOnly_MatchesThatFileCaseInsensitivelyAndNothingElse()
    {
        Assert.True(VsDebuggerChannelRules.MatchesBreakpointRemoval(@"C:\repo\A.cs", null, @"c:\REPO\a.CS", 7));
        Assert.False(VsDebuggerChannelRules.MatchesBreakpointRemoval(@"C:\repo\A.cs", null, @"C:\repo\B.cs", 7));
        Assert.False(VsDebuggerChannelRules.MatchesBreakpointRemoval(@"C:\repo\A.cs", null, null, 7));
    }

    [Fact]
    public void MatchesBreakpointRemoval_PathAndLine_MatchesOnlyThatLine()
    {
        Assert.True(VsDebuggerChannelRules.MatchesBreakpointRemoval(@"C:\repo\A.cs", 42, @"C:\repo\A.cs", 42));
        Assert.False(VsDebuggerChannelRules.MatchesBreakpointRemoval(@"C:\repo\A.cs", 42, @"C:\repo\A.cs", 7));
        Assert.False(VsDebuggerChannelRules.MatchesBreakpointRemoval(@"C:\repo\A.cs", 42, @"C:\repo\B.cs", 42));
    }

    [Fact]
    public void ResolveStackFrameItemIndex_IsOneBasedAndAgreesWithGetCallStackNumbering()
    {
        Assert.Equal(1, VsDebuggerChannelRules.ResolveStackFrameItemIndex(0, 3));
        Assert.Equal(2, VsDebuggerChannelRules.ResolveStackFrameItemIndex(1, 3));
        Assert.Equal(3, VsDebuggerChannelRules.ResolveStackFrameItemIndex(2, 3));
    }

    [Fact]
    public void ResolveStackFrameItemIndex_OutOfRange_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => VsDebuggerChannelRules.ResolveStackFrameItemIndex(3, 3));
        Assert.Throws<InvalidOperationException>(() => VsDebuggerChannelRules.ResolveStackFrameItemIndex(-1, 3));
        Assert.Throws<InvalidOperationException>(() => VsDebuggerChannelRules.ResolveStackFrameItemIndex(0, 0));
    }

    [Fact]
    public void RemainingMs_ReturnsWhatIsLeftOfTheWindow()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(2_000, VsDebuggerChannelRules.RemainingMs(now.AddMilliseconds(2_000), now));
    }

    [Fact]
    public void RemainingMs_ExpiredWindow_IsZeroNeverNegative()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, VsDebuggerChannelRules.RemainingMs(now.AddMilliseconds(-1), now));
        Assert.Equal(0, VsDebuggerChannelRules.RemainingMs(now.AddSeconds(-30), now));
    }

    [Fact]
    public void ClampEvaluationTimeoutMs_NeverYieldsDtesUndefinedZero()
    {
        Assert.Equal(1, VsDebuggerChannelRules.ClampEvaluationTimeoutMs(0, 5_000));
        Assert.Equal(1, VsDebuggerChannelRules.ClampEvaluationTimeoutMs(-1, 5_000));
    }

    [Fact]
    public void ClampEvaluationTimeoutMs_HonoursTheCeilingAndPassesLegalValuesThrough()
    {
        Assert.Equal(5_000, VsDebuggerChannelRules.ClampEvaluationTimeoutMs(120_000, 5_000));
        Assert.Equal(2_000, VsDebuggerChannelRules.ClampEvaluationTimeoutMs(2_000, 5_000));
    }

    [Fact]
    public void TruncateDebuggeeValue_ShortEnoughValuesAreUntouched()
    {
        Assert.Equal(string.Empty, VsDebuggerChannelRules.TruncateDebuggeeValue(null, 4));
        Assert.Equal("abc", VsDebuggerChannelRules.TruncateDebuggeeValue("abc", 4));
        Assert.Equal("abcd", VsDebuggerChannelRules.TruncateDebuggeeValue("abcd", 4));
    }

    [Fact]
    public void TruncateDebuggeeValue_LongValueIsCutAndMarked()
    {
        Assert.Equal("abcd…", VsDebuggerChannelRules.TruncateDebuggeeValue("abcdef", 4));
    }

    [Fact]
    public void TruncateDebuggeeValue_NeverEmitsHalfOfASurrogatePair()
    {
        // "ab😀cd" is a,b,<high>,<low>,c,d - cutting at 3 would strand the high surrogate, which
        // Newtonsoft serializes as a bare \udXXX escape that strict JSON readers reject.
        Assert.Equal("ab…", VsDebuggerChannelRules.TruncateDebuggeeValue("ab\uD83D\uDE00cd", 3));
        Assert.Equal("ab\uD83D\uDE00…", VsDebuggerChannelRules.TruncateDebuggeeValue("ab\uD83D\uDE00cd", 4));
    }

    [Fact]
    public void ModeWireName_UsesTheThreeNamesTheProtocolDocuments()
    {
        Assert.Equal("design", VsDebuggerChannelRules.ModeWireName(VsDebuggerChannelRules.Mode.Design));
        Assert.Equal("run", VsDebuggerChannelRules.ModeWireName(VsDebuggerChannelRules.Mode.Run));
        Assert.Equal("break", VsDebuggerChannelRules.ModeWireName(VsDebuggerChannelRules.Mode.Break));
    }

    [Fact]
    public void BreakWaitTimedOut_OnlyAStillRunningProgramIsATimeout()
    {
        Assert.True(VsDebuggerChannelRules.BreakWaitTimedOut(VsDebuggerChannelRules.Mode.Run));
        // The step landed - this is the inversion that shipped once, reporting a completed step as
        // timed out because a loop re-hit the breakpoint it started on.
        Assert.False(VsDebuggerChannelRules.BreakWaitTimedOut(VsDebuggerChannelRules.Mode.Break));
        // The debuggee exited during the step; the program ending is not the wait expiring.
        Assert.False(VsDebuggerChannelRules.BreakWaitTimedOut(VsDebuggerChannelRules.Mode.Design));
    }

    [Fact]
    public void LaunchTimedOut_OnlyNeverLeavingDesignModeIsATimeout()
    {
        Assert.True(VsDebuggerChannelRules.LaunchTimedOut(VsDebuggerChannelRules.Mode.Design));
        // A program that keeps running launched successfully; it simply never hit a breakpoint.
        Assert.False(VsDebuggerChannelRules.LaunchTimedOut(VsDebuggerChannelRules.Mode.Run));
        Assert.False(VsDebuggerChannelRules.LaunchTimedOut(VsDebuggerChannelRules.Mode.Break));
    }
}
