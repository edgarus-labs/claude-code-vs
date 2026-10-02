using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Dependency-free decision logic of the VS control channel's debugger methods.
/// </summary>
public static class VsDebuggerChannelRules
{
    /// <summary>
    /// Validates a <c>removeBreakpoint</c> request's raw wire arguments before anything is deleted.
    /// Throws <see cref="InvalidOperationException"/> for a member other than <c>path</c> or
    /// <c>line</c>, a present <c>path</c> that is null or blank, a present <c>line</c> that is null,
    /// or a <c>line</c> without a <c>path</c>. Omitting both members is valid and requests removal of
    /// every breakpoint.
    /// </summary>
    /// <param name="suppliedArgumentNames">Every member name present in the request object, a
    /// JSON-null member included.</param>
    /// <param name="requestedPath">The raw <c>path</c> value, or null when the member is absent or null.</param>
    /// <param name="line">The raw <c>line</c> value, or null when the member is absent or null.</param>
    public static void RequireRemovalArguments(IEnumerable<string> suppliedArgumentNames, string? requestedPath, int? line)
    {
        if (suppliedArgumentNames is null)
        {
            throw new ArgumentNullException(nameof(suppliedArgumentNames));
        }

        var pathSupplied = false;
        var lineSupplied = false;
        foreach (var name in suppliedArgumentNames)
        {
            if (string.Equals(name, "path", StringComparison.Ordinal))
            {
                pathSupplied = true;
            }
            else if (string.Equals(name, "line", StringComparison.Ordinal))
            {
                lineSupplied = true;
            }
            else
            {
                throw new InvalidOperationException(
                    $"'{name}' is not a removeBreakpoint parameter; the parameters are 'path' and 'line'. Omitting both removes every breakpoint, so an unrecognised member is rejected instead of becoming that request.");
            }
        }

        if (pathSupplied && string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new InvalidOperationException("'path' must name a file; omit it entirely to remove every breakpoint.");
        }

        if (lineSupplied && !line.HasValue)
        {
            throw new InvalidOperationException("'line' must be a line number; omit it entirely to remove every breakpoint in the file.");
        }

        RequireLineHasPath(requestedPath, line);
    }

    /// <summary>Whether one breakpoint matches a <c>removeBreakpoint</c> request, against the
    /// already-resolved path. A request with neither path nor line matches every breakpoint. Throws
    /// <see cref="InvalidOperationException"/> for a line without a path.</summary>
    public static bool MatchesBreakpointRemoval(string? resolvedPath, int? line, string? breakpointFile, int breakpointLine)
    {
        RequireLineHasPath(resolvedPath, line);
        if (resolvedPath is null)
        {
            return true;
        }

        return string.Equals(breakpointFile, resolvedPath, StringComparison.OrdinalIgnoreCase)
            && (!line.HasValue || breakpointLine == line.Value);
    }

    private static void RequireLineHasPath(string? path, int? line)
    {
        if (line.HasValue && path is null)
        {
            throw new InvalidOperationException("'line' requires 'path'; omit both to remove every breakpoint.");
        }
    }

    /// <summary>The three debugger modes this channel reports.</summary>
    public enum Mode
    {
        Design,
        Run,
        Break,
    }

    /// <summary>The wire name of a mode.</summary>
    public static string ModeWireName(Mode mode) => mode switch
    {
        Mode.Break => "break",
        Mode.Run => "run",
        _ => "design",
    };

    /// <summary>Whether a wait for the debuggee to stop (<c>continueDebugging</c>, the three steps
    /// and <c>waitForBreak</c>) expired: true only when the program is still running. Break mode and
    /// design mode are not timeouts.</summary>
    public static bool BreakWaitTimedOut(Mode observedMode) => observedMode == Mode.Run;

    /// <summary>Whether a <c>startDebugging</c> launch expired: true only when the debugger never
    /// left design mode.</summary>
    public static bool LaunchTimedOut(Mode observedMode) => observedMode == Mode.Design;

    /// <summary>
    /// Maps a wire <c>frameIndex</c> (0 = innermost frame, as <c>getCallStack</c> numbers its
    /// frames) to the 1-based index EnvDTE's <c>StackFrames.Item</c> takes. Throws
    /// <see cref="InvalidOperationException"/> when <paramref name="frameIndex"/> is outside the
    /// stack.
    /// </summary>
    public static int ResolveStackFrameItemIndex(int frameIndex, int frameCount)
    {
        if (frameIndex < 0 || frameIndex >= frameCount)
        {
            throw new InvalidOperationException($"frameIndex {frameIndex} is out of range (stack has {frameCount} frames).");
        }

        return frameIndex + 1;
    }

    /// <summary>Milliseconds left until <paramref name="deadlineUtc"/>. Never negative, and
    /// saturates at <see cref="int.MaxValue"/>.</summary>
    public static int RemainingMs(DateTime deadlineUtc, DateTime nowUtc)
    {
        double remaining = (deadlineUtc - nowUtc).TotalMilliseconds;
        if (remaining <= 0)
        {
            return 0;
        }

        return remaining >= int.MaxValue ? int.MaxValue : (int)remaining;
    }

    /// <summary>Clamps an agent-supplied expression-evaluation timeout to the range 1 to
    /// <paramref name="maxMs"/>.</summary>
    public static int ClampEvaluationTimeoutMs(int requestedMs, int maxMs) => Math.Max(1, Math.Min(maxMs, requestedMs));

    /// <summary>Bounds an agent-supplied poll wait (<c>waitForBreakMs</c>, <c>timeoutMs</c>): uses
    /// <paramref name="defaultMs"/> when none is given and clamps the result to the range
    /// <paramref name="minMs"/> to <paramref name="maxMs"/>.</summary>
    public static int ClampWaitMs(int? requestedMs, int defaultMs, int minMs, int maxMs) => Math.Max(minMs, Math.Min(maxMs, requestedMs ?? defaultMs));

    /// <summary>Returns <paramref name="line"/> when it is 1 or greater; otherwise throws
    /// <see cref="InvalidOperationException"/>.</summary>
    public static int RequireBreakpointLine(int line)
    {
        if (line < 1)
        {
            throw new InvalidOperationException($"'line' must be 1 or greater; got {line}.");
        }

        return line;
    }

    /// <summary>Caps a debuggee-rendered value at <paramref name="maxChars"/> characters, appending an
    /// ellipsis when truncated, without splitting a surrogate pair. Returns an empty string for
    /// null.</summary>
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
