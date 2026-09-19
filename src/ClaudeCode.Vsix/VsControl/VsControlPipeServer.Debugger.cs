using ClaudeCode.Contracts;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
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
    private const int _maxWaitMs = 120_000;
    private const int _startDebuggingTimeoutMs = 60_000;
    private const int _defaultWaitForBreakMs = 5_000;
    private const int _maxStackFrames = 100;
    private const int _maxLocals = 200;
    private const int _maxValueChars = 1_000;

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

        var configuration = args["configuration"]?.Value<string>();
        if (!string.IsNullOrEmpty(configuration))
        {
            await TrySetActiveConfigurationAsync(configuration!);
        }

        var projectName = args["projectName"]?.Value<string>();
        if (!string.IsNullOrEmpty(projectName))
        {
            await SetStartupProjectAsync(projectName!);
        }

        var built = await VS.Build.BuildSolutionAsync(BuildAction.Build);
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

        var mode = await WaitForModeAsync(debugger, m => m != dbgDebugMode.dbgDesignMode, _startDebuggingTimeoutMs, cancellationToken);
        if (mode == dbgDebugMode.dbgRunMode && waitForBreakMs > 0)
        {
            await WaitForModeAsync(debugger, m => m != dbgDebugMode.dbgRunMode, waitForBreakMs, cancellationToken);
        }

        return DescribeDebugger(debugger);
    }

    private static async Task SetStartupProjectAsync(string projectName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE2>();
        var project = FindDteProject(dte.Solution.Projects, projectName)
            ?? throw new InvalidOperationException($"No project named '{projectName}' is loaded in the solution.");
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

        debugger.Stop(WaitForDesignMode: false);
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
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method

        string? fullPath = null;
        if (!string.IsNullOrEmpty(path))
        {
            using var pathLease = WorkspacePathGuard.AcquireDocument(_workspaceRoot, path!);
            fullPath = pathLease.FullPath;
        }

        var doomed = new List<Breakpoint>();
        foreach (Breakpoint breakpoint in debugger.Breakpoints)
        {
            if (fullPath is null
                || (string.Equals(breakpoint.File, fullPath, StringComparison.OrdinalIgnoreCase)
                    && (!line.HasValue || breakpoint.FileLine == line.Value)))
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
        var timedOut = !LeftTheBreak(mode);
        if (!timedOut)
        {
            var remainingMs = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
            mode = await WaitForModeAsync(debugger, m => m != dbgDebugMode.dbgRunMode, remainingMs, cancellationToken);
            timedOut = mode == dbgDebugMode.dbgRunMode;
        }

        return DescribeDebugger(debugger, timedOut);
    }

    private static async Task<JObject> WaitForBreakAsync(JObject args, CancellationToken cancellationToken)
    {
        var timeoutMs = ReadWaitMs(args, "timeoutMs", 10_000);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var mode = await WaitForModeAsync(debugger, m => m != dbgDebugMode.dbgRunMode, timeoutMs, cancellationToken);
        return DescribeDebugger(debugger, timedOut: mode == dbgDebugMode.dbgRunMode);
    }

    private static async Task<JObject> GetCallStackAsync()
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        RequireBreakMode(debugger);

        var frames = new JArray();
        var index = 0;
        foreach (StackFrame frame in debugger.CurrentThread.StackFrames)
        {
            if (index >= _maxStackFrames) break;
            var json = FrameToJson(frame);
            json["index"] = index++;
            frames.Add(json);
        }

        return new JObject
        {
            ["threadId"] = debugger.CurrentThread.ID,
            ["threadName"] = debugger.CurrentThread.Name,
            ["frames"] = frames,
        };
    }

    private static async Task<JObject> GetLocalsAsync(JObject args)
    {
        var frameIndex = Math.Max(0, args["frameIndex"]?.Value<int?>() ?? 0);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        RequireBreakMode(debugger);

        StackFrame? frame = frameIndex == 0 ? debugger.CurrentStackFrame : null;
        if (frame is null)
        {
            var frames = debugger.CurrentThread.StackFrames;
            if (frameIndex >= frames.Count)
            {
                throw new InvalidOperationException($"frameIndex {frameIndex} is out of range (stack has {frames.Count} frames).");
            }

            frame = frames.Item(frameIndex + 1);
        }

        var locals = new JArray();
        var count = 0;
        foreach (Expression local in frame.Locals)
        {
            if (count++ >= _maxLocals) break;
            locals.Add(ExpressionToJson(local));
        }

        var result = FrameToJson(frame);
        result["index"] = frameIndex;
        result["locals"] = locals;
        return result;
    }

    private static async Task<JObject> EvaluateExpressionAsync(JObject args)
    {
        var expression = RequireString(args, "expression");
        var timeoutMs = ReadWaitMs(args, "timeoutMs", 3_000);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // the analyzer needs the switch visible in this method
        RequireBreakMode(debugger);

        var evaluated = debugger.GetExpression(expression, UseAutoExpandRules: true, Timeout: timeoutMs);
        var result = ExpressionToJson(evaluated);
        result["expression"] = expression;
        return result;
    }

    private static JObject ExpressionToJson(Expression expression)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var value = expression.Value ?? string.Empty;
        if (value.Length > _maxValueChars)
        {
            value = value.Substring(0, _maxValueChars) + "…";
        }

        return new JObject
        {
            ["name"] = expression.Name,
            ["type"] = expression.Type,
            ["value"] = value,
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

    /// <summary>Identifies the break the debugger is stopped at (reason, frame and line). A step that
    /// completes between two polls is never observed as run mode, so <see cref="StepAsync"/> compares
    /// this token to tell a finished step from one that has not started yet.</summary>
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
        var mode = debugger.CurrentMode;
        var result = new JObject
        {
            ["mode"] = ModeName(mode),
            ["processes"] = DebuggedProcessesToJson(debugger),
        };

        if (mode == dbgDebugMode.dbgBreakMode)
        {
            result["reason"] = BreakReasonName(debugger.LastBreakReason);
            try
            {
                if (debugger.CurrentStackFrame is StackFrame frame)
                {
                    result["currentFrame"] = FrameToJson(frame);
                }
            }
            catch (COMException)
            {
                // No managed frame available (e.g. stopped in native/external code).
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
        foreach (Process process in debugger.DebuggedProcesses)
        {
            processes.Add(new JObject { ["id"] = process.ProcessID, ["name"] = process.Name });
        }

        return processes;
    }

    private static HashSet<int> GetDebuggedProcessIds(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var ids = new HashSet<int>();
        foreach (Process process in debugger.DebuggedProcesses)
        {
            ids.Add(process.ProcessID);
        }

        return ids;
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

    private static string ModeName(dbgDebugMode mode) => mode switch
    {
        dbgDebugMode.dbgBreakMode => "break",
        dbgDebugMode.dbgRunMode => "run",
        _ => "design",
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
