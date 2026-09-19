using System;

namespace ClaudeCode.Contracts;

/// <summary>
/// Dependency-free decision logic of the VS control channel's debugger methods
/// (<c>VsControlPipeServer.Debugger.cs</c>). Everything here is reachable from an untrusted ACP
/// agent request, so it is kept out of the EnvDTE glue where it can be unit-tested: a mistake in
/// the breakpoint match predicate silently destroys user state, and a mistake in the frame
/// arithmetic silently reports the wrong frame's locals.
/// </summary>
public static class VsDebuggerChannelRules
{
    /// <summary>
    /// Validates a <c>removeBreakpoint</c> request before anything is deleted. A <c>line</c>
    /// without a <c>path</c> is ambiguous: the wire schema allows it, but there is no sane
    /// interpretation of "line 42 in every file", and the only non-throwing reading - delete
    /// everything - destroys breakpoints the user set by hand and cannot undo.
    /// </summary>
    public static void RequireRemovalArguments(string? resolvedPath, int? line)
    {
        if (line.HasValue && resolvedPath is null)
        {
            throw new InvalidOperationException("'line' requires 'path'; omit both to remove every breakpoint.");
        }
    }

    /// <summary>Whether one breakpoint matches a <c>removeBreakpoint</c> request. A request with
    /// neither path nor line is the documented "remove every breakpoint" form.</summary>
    public static bool MatchesBreakpointRemoval(string? resolvedPath, int? line, string? breakpointFile, int breakpointLine)
    {
        RequireRemovalArguments(resolvedPath, line);
        if (resolvedPath is null)
        {
            return true;
        }

        return string.Equals(breakpointFile, resolvedPath, StringComparison.OrdinalIgnoreCase)
            && (!line.HasValue || breakpointLine == line.Value);
    }

    /// <summary>
    /// Maps a wire <c>frameIndex</c> (0 = innermost frame, exactly how <c>getCallStack</c> numbers
    /// its frames) to the 1-based index EnvDTE's <c>StackFrames.Item</c> takes. Index 0 must not be
    /// answered from <c>Debugger.CurrentStackFrame</c>: that is the frame *selected* in the Call
    /// Stack window, which Just My Code moves off the innermost frame, so the two numberings would
    /// disagree about which frame the locals belong to.
    /// </summary>
    public static int ResolveStackFrameItemIndex(int frameIndex, int frameCount)
    {
        if (frameIndex < 0 || frameIndex >= frameCount)
        {
            throw new InvalidOperationException($"frameIndex {frameIndex} is out of range (stack has {frameCount} frames).");
        }

        return frameIndex + 1;
    }

    /// <summary>Milliseconds left of a wait budget, for handing the remainder of one wall-clock
    /// window to a second poll. Never negative, and never wraps on the cast.</summary>
    public static int RemainingMs(DateTime deadlineUtc, DateTime nowUtc)
    {
        double remaining = (deadlineUtc - nowUtc).TotalMilliseconds;
        if (remaining <= 0)
        {
            return 0;
        }

        return remaining >= int.MaxValue ? int.MaxValue : (int)remaining;
    }

    /// <summary>Bounds an agent-supplied expression-evaluation timeout. Evaluation is a synchronous
    /// UI-thread call, so its ceiling is far below the poll-wait ceiling. The floor is 1 ms, not 0:
    /// DTE's <c>Timeout</c> uses -1 as its "wait forever" sentinel and leaves 0 undefined.</summary>
    public static int ClampEvaluationTimeoutMs(int requestedMs, int maxMs)
    {
        return Math.Max(1, Math.Min(maxMs, requestedMs));
    }

    /// <summary>Caps a debuggee-rendered value for the wire without splitting a surrogate pair: a
    /// stranded high surrogate serializes as a bare <c>\udXXX</c> escape that strict JSON readers
    /// on the agent side reject.</summary>
    public static string TruncateDebuggeeValue(string? value, int maxChars)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        int cut = maxChars > 0 && char.IsHighSurrogate(value[maxChars - 1]) ? maxChars - 1 : maxChars;
        return value.Substring(0, cut) + "…";
    }
}
