using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Dependency-free decision logic of the VS control channel's debugger methods
/// (<c>VsControlPipeServer.Debugger.cs</c>). Everything here is reachable from an untrusted ACP
/// agent request, so it is kept out of the EnvDTE glue where it can be unit-tested: a mistake in
/// the breakpoint removal rules silently destroys user state nothing restores, a mistake in the
/// frame arithmetic silently reports the wrong frame's locals, and a mistake in the
/// <c>timedOut</c> derivation - which has inverted once - silently lies about whether an
/// operation finished.
/// </summary>
public static class VsDebuggerChannelRules
{
    /// <summary>
    /// Validates a <c>removeBreakpoint</c> request's raw wire arguments before anything is deleted.
    /// Visual Studio has no undo for a deleted breakpoint and the user's own breakpoints sit in the
    /// same collection, so every way this request can silently widen into "delete everything"
    /// is rejected: a <c>line</c> without a <c>path</c> ("line 42 in every file" has no sane
    /// reading), a present-but-blank or present-but-null <c>path</c> (a mis-serialised file name
    /// is not an omission, and an explicit JSON null - how tool calls routinely spell an omitted
    /// optional - reads back from the wire exactly like one), a present-but-null <c>line</c> (which
    /// would widen one line to the whole file), and a member the method does not define
    /// (<c>file</c>, <c>filePath</c>), which leaves zero recognised arguments. Every other method on
    /// this channel degrades an unknown member to a missing optional; this is the one where
    /// dropping the arguments escalates instead. Omitting both members remains the documented
    /// "remove every breakpoint" form.
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
    /// already-resolved path. A request with neither path nor line is the documented "remove every
    /// breakpoint" form.</summary>
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

    /// <summary>The invariant both the up-front validation and the match predicate rest on, so an
    /// empty breakpoint list can never answer "removed 0" to a request that should have been
    /// rejected.</summary>
    private static void RequireLineHasPath(string? path, int? line)
    {
        if (line.HasValue && path is null)
        {
            throw new InvalidOperationException("'line' requires 'path'; omit both to remove every breakpoint.");
        }
    }

    /// <summary>The three debugger modes this channel reports. EnvDTE's <c>dbgDebugMode</c> is
    /// mapped onto it at the host boundary so the <c>mode</c> a response carries and the
    /// <c>timedOut</c> flag beside it are derived from one value and cannot disagree.</summary>
    public enum Mode
    {
        Design,
        Run,
        Break,
    }

    /// <summary>The wire name of a mode, as docs/VsControlProtocol.md documents it.</summary>
    public static string ModeWireName(Mode mode) => mode switch
    {
        Mode.Break => "break",
        Mode.Run => "run",
        _ => "design",
    };

    /// <summary>Whether a wait for the debuggee to stop - <c>continueDebugging</c>, the three steps
    /// and <c>waitForBreak</c> - expired. <c>timedOut</c> means exactly what the protocol documents:
    /// the program is still running. Break mode is the step having landed, including a loop
    /// re-hitting the breakpoint it started on - which is why the break-position token must never
    /// decide this flag - and design mode is the debuggee having exited. Neither is a timeout.</summary>
    public static bool BreakWaitTimedOut(Mode observedMode) => observedMode == Mode.Run;

    /// <summary>Whether a <c>startDebugging</c> launch expired. Only never leaving design mode is a
    /// failed launch: a program that keeps running launched successfully and simply never hit a
    /// breakpoint.</summary>
    public static bool LaunchTimedOut(Mode observedMode) => observedMode == Mode.Design;

    /// <summary>
    /// Maps a wire <c>frameIndex</c> (0 = innermost frame, exactly how <c>getCallStack</c> numbers
    /// its frames) to the 1-based index EnvDTE's <c>StackFrames.Item</c> takes. The index must be
    /// resolved against the raw call stack and never read back from
    /// <c>Debugger.CurrentStackFrame</c>: that is the frame *selected* in the Call Stack window,
    /// which Just My Code moves off the innermost frame, so the two numberings would disagree about
    /// which frame the locals belong to. The host then makes the resolved frame the selected one,
    /// which is what keeps <c>evaluateExpression</c> and the state's <c>currentFrame</c> agreeing
    /// with it.
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

    /// <summary>Bounds an agent-supplied poll wait (<c>waitForBreakMs</c>, <c>timeoutMs</c>). The
    /// ceiling is the one number the client transport budget, the tool schema and the host all have
    /// to agree on. The floor is 0 for a plain wait; the step methods pass one poll interval, because
    /// a step whose first poll runs before the engine has left the pre-step break would otherwise
    /// report the old frame as the landed step.</summary>
    public static int ClampWaitMs(int? requestedMs, int defaultMs, int minMs, int maxMs)
    {
        return Math.Max(minMs, Math.Min(maxMs, requestedMs ?? defaultMs));
    }

    /// <summary>Breakpoint lines are 1-based; <c>Breakpoints.Add</c> answers anything below that with
    /// a raw HRESULT the agent cannot act on.</summary>
    public static int RequireBreakpointLine(int line)
    {
        if (line < 1)
        {
            throw new InvalidOperationException($"'line' must be 1 or greater; got {line}.");
        }

        return line;
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
