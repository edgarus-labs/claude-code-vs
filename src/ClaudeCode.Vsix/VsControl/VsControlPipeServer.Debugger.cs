using ClaudeCode.Contracts;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Debugger = EnvDTE.Debugger;
using Process = EnvDTE.Process;

namespace ClaudeCode.Vsix.VsControl;

/// <summary>Debugger methods of the VS control channel (EnvDTE <see cref="Debugger"/>). Every wait is an
/// async poll of <see cref="Debugger.CurrentMode"/>: the VS UI thread is never blocked, because the
/// debugger itself needs it to reach break mode.</summary>
internal sealed partial class VsControlPipeServer
{
    private enum DebuggerStep { Continue, Over, Into, Out }

    private const int _debuggerPollMs = 100;
    // Every poll wait must finish inside the MCP client's 60 s per-request timeout
    // (VsControlPipeClient._requestTimeout): a wait the transport abandons also keeps the pipe's
    // serial read loop busy, so the agent's next request - stopDebugging included - is not even
    // read until it expires. Agreed with the client owner as one number; the tool schema advertises
    // the same 45 s ceiling.
    private const int _maxWaitMs = 45_000;
    // startDebugging builds before it launches, so the client grants it the build budget
    // (VsControlPipeClient._buildTimeout, 12 min / 720 s) instead of the 60 s default; that is what
    // lets this one wait exceed _maxWaitMs. The whole method stays inside that budget: RunBuildAsync
    // caps the build at 10 min, this launch wait adds 60 s and waitForBreakMs at most _maxWaitMs, so
    // the server's worst case is 705 s and the agent sees Visual Studio's own actionable error
    // rather than a transport timeout invented while it is still working.
    private const int _startDebuggingTimeoutMs = 60_000;
    private const int _defaultWaitForBreakMs = 5_000;
    private const int _maxStackFrames = 100;
    private const int _maxLocals = 200;
    private const int _maxValueChars = 1_000;
    // Expression evaluation is a synchronous UI-thread COM call - EnvDTE's debugger automation is
    // UI-thread-affinitized and offers no off-thread entry point - so devenv is frozen for its
    // whole duration. It gets a far smaller ceiling than a poll wait, which blocks nothing.
    private const int _defaultEvaluationMs = 3_000;
    private const int _maxEvaluationMs = 5_000;
    // getLocals renders up to _maxLocals values through that same synchronous evaluator. The bound
    // is checked between values and no individual Expression.Value read takes a timeout, so it caps
    // the walk, not the whole call: the worst case is acquiring frame.Locals, plus this budget, plus
    // the one value read that is in flight when it expires.
    private const int _maxLocalsWalkMs = 5_000;

    private static async Task<Debugger> GetDebuggerAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE2>();
        return dte.Debugger;
    }

    /// <summary>Builds first, then starts: a failed build would otherwise pop VS's modal "There were
    /// build errors. Continue?" prompt, which nothing on this channel could answer.</summary>
    private static async Task<JObject> StartDebuggingAsync(JObject args, CancellationToken cancellationToken)
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
        {
            throw new InvalidOperationException("A debugging session is already active; use stopDebugging first.");
        }

        // Validate before mutating: resolving projectName is the only step that can reject the
        // request, and it must not run after the user's active solution configuration has already
        // been switched to one they never selected (VsControlPipeServer.Build.cs honours the same
        // rule for buildSolution).
        var projectName = args["projectName"]?.Value<string>();
        var startupProject = string.IsNullOrEmpty(projectName)
            ? null
            : await FindStartupProjectAsync(projectName!);

        var configuration = args["configuration"]?.Value<string>();
        if (!string.IsNullOrEmpty(configuration))
        {
            await TrySetActiveConfigurationAsync(configuration!);
        }

        if (startupProject is not null)
        {
            await SetStartupProjectAsync(startupProject);
        }

        // Through the same backstop buildSolution/buildProject use, which also translates the two
        // non-success exits: the toolkit's completion source is signalled from an
        // IVsUpdateSolutionEvents sink that never fires for a build Visual Studio skips, and requests
        // are served strictly one at a time, so an unbounded await here would wedge the whole control
        // channel for the rest of the session and block DisposeAsync on the listen loop.
        var built = await RunBuildAsync(() => VS.Build.BuildSolutionAsync(BuildAction.Build), cancellationToken);
        if (!built)
        {
            var (errorCount, warningCount) = await CountBuildDiagnosticsAsync();
            throw new InvalidOperationException(
                $"Build failed ({errorCount} error(s), {warningCount} warning(s)); debugging was not started. Use getBuildErrors.");
        }

        var waitForBreakMs = ReadWaitMs(args, "waitForBreakMs", 3_000);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            debugger.Go(WaitForBreakOrEnd: false);
        }
        catch (COMException)
        {
            throw new InvalidOperationException("Debugging could not be started; check that the solution has a runnable startup project.");
        }

        var launchMode = await WaitForModeAsync(debugger, m => m != dbgDebugMode.dbgDesignMode, _startDebuggingTimeoutMs, cancellationToken);
        if (launchMode == dbgDebugMode.dbgRunMode && waitForBreakMs > 0)
        {
            // A program that keeps running is not a failed launch, so this wait expiring is not a
            // timeout - only never leaving design mode is.
            await WaitForModeAsync(debugger, m => m != dbgDebugMode.dbgRunMode, waitForBreakMs, cancellationToken);
        }

        return DescribeDebugger(debugger, timedOut: VsDebuggerChannelRules.LaunchTimedOut(ToMode(launchMode)));
    }

    /// <summary>Resolves a startup-project name against the loaded solution. Split from
    /// <see cref="SetStartupProjectAsync"/> so the one step that can reject the request runs before
    /// anything the request would have to undo.</summary>
    private static async Task<EnvDTE.Project> FindStartupProjectAsync(string projectName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE2>();
        return FindDteProject(dte.Solution.Projects, projectName)
            ?? throw new InvalidOperationException($"No project named '{projectName}' is loaded in the solution.");
    }

    private static async Task SetStartupProjectAsync(EnvDTE.Project project)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE2>();
        dte.Solution.SolutionBuild.StartupProjects = project.UniqueName;
    }

    private static EnvDTE.Project? FindDteProject(Projects projects, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (EnvDTE.Project project in projects)
        {
            var found = FindDteProject(project, name);
            if (found is not null) return found;
        }

        return null;
    }

    private static EnvDTE.Project? FindDteProject(EnvDTE.Project project, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return project;
        }

        if (project.Kind == ProjectKinds.vsProjectKindSolutionFolder && project.ProjectItems is not null)
        {
            foreach (ProjectItem item in project.ProjectItems)
            {
                if (item.SubProject is EnvDTE.Project sub)
                {
                    var found = FindDteProject(sub, name);
                    if (found is not null) return found;
                }
            }
        }

        return null;
    }

    private static async Task<JObject> StopDebuggingAsync(CancellationToken cancellationToken)
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
        {
            return DescribeDebugger(debugger);
        }

        try
        {
            debugger.Stop(WaitForDesignMode: false);
        }
        catch (COMException)
        {
            // The debuggee can exit on its own between the mode read above and this call - reading a
            // DTE property pumps COM messages, so the window is real - and a call into a debug engine
            // that is already tearing down fails with an RPC error. The goal state is what matters:
            // let the wait and DescribeDebugger report what actually happened instead of failing a
            // stop that succeeded and sending the agent back to startDebugging.
        }
        catch (InvalidComObjectException)
        {
            // The debugger RCW itself was released as the session ended.
        }

        await WaitForModeAsync(debugger, m => m == dbgDebugMode.dbgDesignMode, 15_000, cancellationToken);
        return DescribeDebugger(debugger);
    }

    private static async Task<JObject> GetDebuggerStateAsync()
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        return DescribeDebugger(debugger);
    }

    private async Task<JObject> SetBreakpointAsync(JObject args)
    {
        var path = RequireString(args, "path");
        var line = RequireInt(args, "line");
        using var pathLease = WorkspacePathGuard.AcquireDocument(_workspaceRoot, path);
        var condition = args["condition"]?.Value<string>() ?? string.Empty;

        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        _ = debugger.Breakpoints.Add(
            File: pathLease.FullPath,
            Line: line,
            Condition: condition,
            ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);

        // The collection Add() returns does not enumerate reliably; report what the debugger now has
        // on that file (the line may have been moved to the nearest executable statement).
        var result = new JArray();
        foreach (var breakpoint in BreakpointsToJson(debugger.Breakpoints))
        {
            if (string.Equals(breakpoint["file"]?.Value<string>(), pathLease.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(breakpoint);
            }
        }

        return new JObject { ["breakpoints"] = result };
    }

    private async Task<JObject> RemoveBreakpointAsync(JObject args)
    {
        var path = args["path"]?.Value<string>();
        var line = args["line"]?.Value<int?>();

        // Validate the raw request before a single breakpoint is deleted. An empty 'path', a 'line'
        // without a 'path' and a member this method does not define all otherwise collapse into the
        // documented "remove every breakpoint" form, which takes the breakpoints the user set by
        // hand with it and cannot be undone. Checking here rather than at the first match also stops
        // an empty breakpoint list from answering "removed 0" to a request that is simply wrong.
        VsDebuggerChannelRules.RequireRemovalArguments(args.Properties().Select(p => p.Name), path, line);

        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method

        string? fullPath = null;
        if (path is not null)
        {
            using var pathLease = WorkspacePathGuard.AcquireDocument(_workspaceRoot, path);
            fullPath = pathLease.FullPath;
        }

        var doomed = new List<Breakpoint>();
        foreach (Breakpoint breakpoint in debugger.Breakpoints)
        {
            if (VsDebuggerChannelRules.MatchesBreakpointRemoval(fullPath, line, breakpoint.File, breakpoint.FileLine))
            {
                doomed.Add(breakpoint);
            }
        }

        foreach (var breakpoint in doomed)
        {
            breakpoint.Delete();
        }

        return new JObject { ["removed"] = doomed.Count };
    }

    private static async Task<JObject> ListBreakpointsAsync()
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        return new JObject { ["breakpoints"] = BreakpointsToJson(debugger.Breakpoints) };
    }

    private static JArray BreakpointsToJson(Breakpoints breakpoints)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var result = new JArray();
        foreach (Breakpoint breakpoint in breakpoints)
        {
            result.Add(new JObject
            {
                ["file"] = breakpoint.File,
                ["line"] = breakpoint.FileLine,
                ["enabled"] = breakpoint.Enabled,
                ["condition"] = string.IsNullOrEmpty(breakpoint.Condition) ? null : breakpoint.Condition,
                ["hitCount"] = breakpoint.CurrentHits,
                ["function"] = string.IsNullOrEmpty(breakpoint.FunctionName) ? null : breakpoint.FunctionName,
            });
        }

        return result;
    }

    private static async Task<JObject> StepAsync(JObject args, DebuggerStep step, CancellationToken cancellationToken)
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new InvalidOperationException("The debugger is not in break mode.");
        }

        var waitForBreakMs = ReadWaitMs(args, "waitForBreakMs", _defaultWaitForBreakMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(waitForBreakMs);
        var startedAt = BreakPositionToken(debugger);
        switch (step)
        {
            case DebuggerStep.Continue: debugger.Go(WaitForBreakOrEnd: false); break;
            case DebuggerStep.Over: debugger.StepOver(WaitForBreakOrEnd: false); break;
            case DebuggerStep.Into: debugger.StepInto(WaitForBreakOrEnd: false); break;
            case DebuggerStep.Out: debugger.StepOut(WaitForBreakOrEnd: false); break;
        }

        // The engine is still in break mode for a moment after the step is issued, so waiting only for
        // "not running" is satisfied by the pre-step state on the first poll and reports the stale
        // frame. Wait for this break to end first: the debugger either enters run mode or - for a step
        // that finishes inside one poll interval - reports a different break position.
        bool LeftTheBreak(dbgDebugMode observed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return observed != dbgDebugMode.dbgBreakMode || BreakPositionToken(debugger) != startedAt;
        }

        var mode = await WaitForModeAsync(debugger, LeftTheBreak, waitForBreakMs, cancellationToken);
        if (mode == dbgDebugMode.dbgRunMode)
        {
            mode = await WaitForModeAsync(
                debugger,
                m => m != dbgDebugMode.dbgRunMode,
                VsDebuggerChannelRules.RemainingMs(deadline, DateTime.UtcNow),
                cancellationToken);
        }

        // timedOut means exactly what the protocol documents: the program is still running. The
        // break position only ends the first wait early - it must never decide the flag, because it
        // repeats when a loop re-hits the same breakpoint and is empty on both sides in native
        // code, which reported a timeout for a step that had in fact already completed.
        return DescribeDebugger(debugger, timedOut: VsDebuggerChannelRules.BreakWaitTimedOut(ToMode(mode)));
    }

    private static async Task<JObject> WaitForBreakAsync(JObject args, CancellationToken cancellationToken)
    {
        var timeoutMs = ReadWaitMs(args, "timeoutMs", 10_000);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var mode = await WaitForModeAsync(debugger, m => m != dbgDebugMode.dbgRunMode, timeoutMs, cancellationToken);
        return DescribeDebugger(debugger, timedOut: VsDebuggerChannelRules.BreakWaitTimedOut(ToMode(mode)));
    }

    private static async Task<JObject> GetCallStackAsync()
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        RequireBreakMode(debugger);
        var thread = RequireCurrentThread(debugger);

        var frames = new JArray();
        var index = 0;
        var truncated = false;
        foreach (StackFrame frame in thread.StackFrames)
        {
            if (index >= _maxStackFrames)
            {
                truncated = true;
                break;
            }

            var json = FrameToJson(frame);
            json["index"] = index++;
            frames.Add(json);
        }

        return new JObject
        {
            ["threadId"] = thread.ID,
            ["threadName"] = thread.Name,
            ["frames"] = frames,
            ["truncated"] = truncated,
        };
    }

    private static async Task<JObject> GetLocalsAsync(JObject args)
    {
        var frameIndex = args["frameIndex"]?.Value<int?>() ?? 0;
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        RequireBreakMode(debugger);
        var thread = RequireCurrentThread(debugger);

        // getLocals reads the frame it resolved, so it is correct either way; selecting it is what
        // makes a following evaluateExpression resolve identifiers in that same frame.
        var frame = ResolveStackFrame(thread, frameIndex);
        _ = TrySelectStackFrame(debugger, frame);

        var locals = new JArray();
        var count = 0;
        var truncated = false;
        // Started before frame.Locals is touched: acquiring that collection makes the debugger render
        // every local, which under auto-expand runs debuggee ToString/DebuggerDisplay code on this
        // thread. A synchronous COM call cannot be preempted, so this caps the walk rather than the
        // whole request - see _maxLocalsWalkMs.
        var walkDeadline = DateTime.UtcNow.AddMilliseconds(_maxLocalsWalkMs);
        foreach (Expression local in frame.Locals)
        {
            if (count >= _maxLocals || DateTime.UtcNow >= walkDeadline)
            {
                truncated = true;
                break;
            }

            count++;
            locals.Add(ExpressionToJson(local));
        }

        var result = FrameToJson(frame);
        result["index"] = frameIndex;
        result["locals"] = locals;
        result["truncated"] = truncated;
        return result;
    }

    /// <summary>
    /// Resolves <paramref name="frameIndex"/> - 0 = innermost, exactly how <c>getCallStack</c>
    /// numbers its frames - against the raw call stack.
    /// </summary>
    private static StackFrame ResolveStackFrame(EnvDTE.Thread thread, int frameIndex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var stackFrames = thread.StackFrames;
        return stackFrames.Item(VsDebuggerChannelRules.ResolveStackFrameItemIndex(frameIndex, stackFrames.Count));
    }

    /// <summary>
    /// Makes <paramref name="frame"/> the debugger's current stack frame, reporting whether the
    /// engine accepted it. Selecting the frame, rather than only indexing it, is what keeps this
    /// channel's three views of "the current frame" in agreement: EnvDTE evaluates
    /// <c>GetExpression</c> in <c>Debugger.CurrentStackFrame</c> and <see cref="DescribeDebugger"/>
    /// reports <c>currentFrame</c> from it, so an index that reached only <c>getLocals</c> would
    /// hand back one frame's locals while <c>evaluateExpression</c> resolved the same identifiers in
    /// another - Just My Code routinely leaves a frame other than the innermost one selected.
    /// </summary>
    private static bool TrySelectStackFrame(Debugger debugger, StackFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            debugger.CurrentStackFrame = frame;
            return true;
        }
        catch (COMException)
        {
            // The engine refused the selection - mid-transition, or a frame it cannot switch to.
            return false;
        }
    }

    /// <summary>Evaluation runs on the UI thread because EnvDTE's debugger automation has no
    /// off-thread entry point; the defence is <see cref="_maxEvaluationMs"/>, a far smaller ceiling
    /// than the poll waits get, so an agent cannot freeze devenv for the full wait cap. The optional
    /// <c>frameIndex</c> uses the same numbering as <c>getCallStack</c> and <c>getLocals</c> and
    /// selects that frame first; omitted, the expression is evaluated in the frame Visual Studio has
    /// selected, which is the one <c>currentFrame</c> reports.</summary>
    private static async Task<JObject> EvaluateExpressionAsync(JObject args)
    {
        var expression = RequireString(args, "expression");
        var frameIndex = args["frameIndex"]?.Value<int?>();
        var timeoutMs = VsDebuggerChannelRules.ClampEvaluationTimeoutMs(
            args["timeoutMs"]?.Value<int?>() ?? _defaultEvaluationMs,
            _maxEvaluationMs);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        RequireBreakMode(debugger);
        if (frameIndex.HasValue)
        {
            var frame = ResolveStackFrame(RequireCurrentThread(debugger), frameIndex.Value);
            if (!TrySelectStackFrame(debugger, frame))
            {
                // Evaluating anyway would resolve the expression's identifiers in whichever frame
                // Visual Studio still has selected and report the answer as if it came from the
                // requested one.
                throw new InvalidOperationException(
                    $"The debugger could not select frame {frameIndex.Value}; the expression was not evaluated.");
            }
        }

        var evaluated = debugger.GetExpression(expression, UseAutoExpandRules: true, Timeout: timeoutMs);
        var result = ExpressionToJson(evaluated);
        result["expression"] = expression;
        return result;
    }

    private static JObject ExpressionToJson(Expression expression)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return new JObject
        {
            ["name"] = expression.Name,
            ["type"] = expression.Type,
            ["value"] = VsDebuggerChannelRules.TruncateDebuggeeValue(expression.Value, _maxValueChars),
            ["isValid"] = expression.IsValidValue,
        };
    }

    private static void RequireBreakMode(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new InvalidOperationException("The debugger is not in break mode; set a breakpoint and use waitForBreak first.");
        }
    }

    /// <summary>The debuggee can exit, or the user can hit F5, between the break-mode check and the
    /// enumeration - DTE property access pumps COM messages - and CurrentThread is null outside
    /// break mode. Say so instead of letting a NullReferenceException reach the agent.</summary>
    private static EnvDTE.Thread RequireCurrentThread(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return debugger.CurrentThread
            ?? throw new InvalidOperationException("The debugger has no current thread; the session may have ended.");
    }

    /// <summary>Identifies the break the debugger is stopped at (reason, frame and line). A step that
    /// completes between two polls is never observed as run mode, so <see cref="StepAsync"/> compares
    /// this token to end its wait as soon as the step lands. It is a best-effort early exit only: it
    /// repeats when a loop re-hits the same breakpoint and is empty on both sides in native code, so
    /// it must never decide whether the operation timed out.</summary>
    private static string BreakPositionToken(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (debugger.CurrentStackFrame is not StackFrame frame)
            {
                return string.Empty;
            }

            var line = frame is EnvDTE90a.StackFrame2 frame2 ? (long)frame2.LineNumber : 0L;
            return $"{debugger.LastBreakReason}|{frame.FunctionName}|{line}";
        }
        catch (COMException)
        {
            // Mid-transition, or no managed frame (native/external code): the position is unknown.
            return string.Empty;
        }
    }

    private static async Task<dbgDebugMode> WaitForModeAsync(Debugger debugger, Func<dbgDebugMode, bool> done, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            dbgDebugMode? mode = null;
            try
            {
                mode = debugger.CurrentMode;
            }
            catch (COMException)
            {
                // The debugger is mid-transition (message pump busy); poll again.
            }
            catch (InvalidComObjectException)
            {
                // The debugger RCW has been released: the session is over and no later poll can
                // recover it. Design mode is what actually happened, and saying so immediately is
                // what keeps a stopDebugging that succeeded from spending the whole budget and then
                // reporting the debuggee as still running.
                return dbgDebugMode.dbgDesignMode;
            }

            if (mode.HasValue && (done(mode.Value) || DateTime.UtcNow >= deadline))
            {
                return mode.Value;
            }

            if (!mode.HasValue && DateTime.UtcNow >= deadline)
            {
                return dbgDebugMode.dbgRunMode;
            }

            // Yields the UI thread between polls (continuation is posted back through the dispatcher).
            await Task.Delay(_debuggerPollMs, cancellationToken);
        }
    }

    private static JObject DescribeDebugger(Debugger debugger, bool? timedOut = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        dbgDebugMode mode;
        try
        {
            mode = debugger.CurrentMode;
        }
        catch (COMException)
        {
            // The session finished tearing down while it was being described - stopDebugging races
            // exactly this window - and a successful stop must not be reported as an error.
            mode = dbgDebugMode.dbgDesignMode;
        }
        catch (InvalidComObjectException)
        {
            mode = dbgDebugMode.dbgDesignMode;
        }

        var result = new JObject
        {
            ["mode"] = VsDebuggerChannelRules.ModeWireName(ToMode(mode)),
            ["processes"] = DebuggedProcessesToJson(debugger),
        };

        if (mode == dbgDebugMode.dbgBreakMode)
        {
            try
            {
                result["reason"] = BreakReasonName(debugger.LastBreakReason);
                if (debugger.CurrentStackFrame is StackFrame frame)
                {
                    result["currentFrame"] = FrameToJson(frame);
                }
            }
            catch (COMException)
            {
                // No managed frame available (e.g. stopped in native/external code).
            }
            catch (InvalidComObjectException)
            {
                // The session ended between the mode read and the frame read.
            }
        }

        if (timedOut.HasValue)
        {
            result["timedOut"] = timedOut.Value;
        }

        return result;
    }

    private static JArray DebuggedProcessesToJson(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var processes = new JArray();
        try
        {
            foreach (Process process in debugger.DebuggedProcesses)
            {
                processes.Add(new JObject { ["id"] = process.ProcessID, ["name"] = process.Name });
            }
        }
        catch (COMException)
        {
            // A debugged process finished exiting mid-enumeration; report the ones already seen.
        }
        catch (InvalidComObjectException)
        {
            // The debugger RCW itself has already been released.
        }

        return processes;
    }

    private static JObject FrameToJson(StackFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var json = new JObject
        {
            ["function"] = frame.FunctionName,
            ["module"] = frame.Module,
            ["language"] = frame.Language,
        };

        // File/line live on the EnvDTE90a extension of the frame; external/native frames have none.
        if (frame is EnvDTE90a.StackFrame2 frame2)
        {
            try
            {
                var file = frame2.FileName;
                if (!string.IsNullOrEmpty(file))
                {
                    json["file"] = file;
                    json["line"] = (long)frame2.LineNumber;
                }
            }
            catch (COMException)
            {
            }
        }

        return json;
    }

    /// <summary>EnvDTE's mode as the dependency-free <see cref="VsDebuggerChannelRules.Mode"/>, so
    /// the <c>mode</c> a response carries and the <c>timedOut</c> flag beside it are derived from
    /// one value and cannot disagree. Both derivations live in the Contracts rule class because
    /// this assembly has no test project and the <c>timedOut</c> semantics have inverted once.</summary>
    private static VsDebuggerChannelRules.Mode ToMode(dbgDebugMode mode) => mode switch
    {
        dbgDebugMode.dbgBreakMode => VsDebuggerChannelRules.Mode.Break,
        dbgDebugMode.dbgRunMode => VsDebuggerChannelRules.Mode.Run,
        _ => VsDebuggerChannelRules.Mode.Design,
    };

    private static string BreakReasonName(dbgEventReason reason) => reason switch
    {
        dbgEventReason.dbgEventReasonBreakpoint => "breakpoint",
        dbgEventReason.dbgEventReasonExceptionThrown => "exceptionThrown",
        dbgEventReason.dbgEventReasonExceptionNotHandled => "exceptionNotHandled",
        dbgEventReason.dbgEventReasonStep => "step",
        dbgEventReason.dbgEventReasonUserBreak => "userBreak",
        dbgEventReason.dbgEventReasonEndProgram => "endProgram",
        dbgEventReason.dbgEventReasonStopDebugging => "stopDebugging",
        dbgEventReason.dbgEventReasonLaunchProgram => "launchProgram",
        dbgEventReason.dbgEventReasonAttachProgram => "attachProgram",
        dbgEventReason.dbgEventReasonDetachProgram => "detachProgram",
        dbgEventReason.dbgEventReasonGo => "go",
        _ => "none",
    };

    private static int ReadWaitMs(JObject args, string propertyName, int defaultMs)
    {
        var value = args[propertyName]?.Value<int?>() ?? defaultMs;
        return Math.Max(0, Math.Min(_maxWaitMs, value));
    }

    private static int RequireInt(JObject args, string propertyName)
    {
        var value = args[propertyName]?.Value<int?>();
        if (!value.HasValue)
        {
            throw new InvalidOperationException($"Missing required parameter '{propertyName}'.");
        }

        return value.Value;
    }
}
