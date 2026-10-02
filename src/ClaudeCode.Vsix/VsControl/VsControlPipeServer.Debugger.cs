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

internal sealed partial class VsControlPipeServer
{
    private enum DebuggerStep { Continue, Over, Into, Out }

    private const int _debuggerPollMs = 100;
    private const int _maxWaitMs = 45_000;
    private const int _startDebuggingTimeoutMs = 60_000;
    private const int _defaultWaitForBreakMs = 5_000;
    private const int _maxStackFrames = 100;
    private const int _maxLocals = 200;
    private const int _maxValueChars = 1_000;
    private const int _defaultEvaluationMs = 3_000;
    private const int _maxEvaluationMs = 5_000;
    private const int _maxLocalsWalkMs = 5_000;

    private static async Task<Debugger> GetDebuggerAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE2>();
        return dte.Debugger;
    }

    private static async Task<JObject> StartDebuggingAsync(JObject args, CancellationToken cancellationToken)
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
        {
            throw new InvalidOperationException("A debugging session is already active; use stopDebugging first.");
        }

        var waitForBreakMs = ReadWaitMs(args, "waitForBreakMs", 3_000);
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

        var built = await RunBuildAsync(() => VS.Build.BuildSolutionAsync(BuildAction.Build), cancellationToken);
        if (!built)
        {
            var (errorCount, warningCount) = await CountBuildDiagnosticsAsync();
            throw new InvalidOperationException(
                $"Build failed ({errorCount} error(s), {warningCount} warning(s)); debugging was not started. Use getBuildErrors.");
        }

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
        var mode = launchMode;
        if (launchMode == dbgDebugMode.dbgRunMode && waitForBreakMs > 0)
        {
            mode = await WaitForModeAsync(debugger, m => m != dbgDebugMode.dbgRunMode, waitForBreakMs, cancellationToken);
        }

        return DescribeDebugger(debugger, mode, timedOut: VsDebuggerChannelRules.LaunchTimedOut(ToMode(launchMode)));
    }

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
        }
        catch (InvalidComObjectException)
        {
        }

        var mode = await WaitForModeAsync(debugger, m => m == dbgDebugMode.dbgDesignMode, 15_000, cancellationToken);
        return DescribeDebugger(debugger, mode, timedOut: null);
    }

    private static async Task<JObject> GetDebuggerStateAsync()
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        return DescribeDebugger(debugger);
    }

    private async Task<JObject> SetBreakpointAsync(JObject args)
    {
        var path = RequireString(args, "path");
        var line = VsDebuggerChannelRules.RequireBreakpointLine(RequireInt(args, "line"));
        using var pathLease = WorkspacePathGuard.AcquireDocument(_workspaceRoot, path);
        var condition = args["condition"]?.Value<string>() ?? string.Empty;

        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        _ = debugger.Breakpoints.Add(
            File: pathLease.FullPath,
            Line: line,
            Condition: condition,
            ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);

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

        VsDebuggerChannelRules.RequireRemovalArguments(args.Properties().Select(p => p.Name), path, line);

        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

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
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
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

        var waitForBreakMs = ReadWaitMs(args, "waitForBreakMs", _defaultWaitForBreakMs, minMs: _debuggerPollMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(waitForBreakMs);
        var startedAt = BreakPositionToken(debugger);
        switch (step)
        {
            case DebuggerStep.Continue: debugger.Go(WaitForBreakOrEnd: false); break;
            case DebuggerStep.Over: debugger.StepOver(WaitForBreakOrEnd: false); break;
            case DebuggerStep.Into: debugger.StepInto(WaitForBreakOrEnd: false); break;
            case DebuggerStep.Out: debugger.StepOut(WaitForBreakOrEnd: false); break;
        }

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

        return DescribeDebugger(debugger, mode, timedOut: VsDebuggerChannelRules.BreakWaitTimedOut(ToMode(mode)));
    }

    private static async Task<JObject> WaitForBreakAsync(JObject args, CancellationToken cancellationToken)
    {
        var timeoutMs = ReadWaitMs(args, "timeoutMs", 10_000);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var mode = await WaitForModeAsync(debugger, m => m != dbgDebugMode.dbgRunMode, timeoutMs, cancellationToken);
        return DescribeDebugger(debugger, mode, timedOut: VsDebuggerChannelRules.BreakWaitTimedOut(ToMode(mode)));
    }

    private static async Task<JObject> GetCallStackAsync()
    {
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
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
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        RequireBreakMode(debugger);
        var thread = RequireCurrentThread(debugger);

        var frame = ResolveStackFrame(thread, frameIndex);
        _ = TrySelectStackFrame(debugger, frame);

        var locals = new JArray();
        var count = 0;
        var truncated = false;
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

    private static StackFrame ResolveStackFrame(EnvDTE.Thread thread, int frameIndex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var stackFrames = thread.StackFrames;
        return stackFrames.Item(VsDebuggerChannelRules.ResolveStackFrameItemIndex(frameIndex, stackFrames.Count));
    }

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
            return false;
        }
    }

    private static async Task<JObject> EvaluateExpressionAsync(JObject args)
    {
        var expression = RequireString(args, "expression");
        var frameIndex = args["frameIndex"]?.Value<int?>();
        var timeoutMs = VsDebuggerChannelRules.ClampEvaluationTimeoutMs(
            args["timeoutMs"]?.Value<int?>() ?? _defaultEvaluationMs,
            _maxEvaluationMs);
        var debugger = await GetDebuggerAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        RequireBreakMode(debugger);
        if (frameIndex.HasValue)
        {
            var frame = ResolveStackFrame(RequireCurrentThread(debugger), frameIndex.Value);
            if (!TrySelectStackFrame(debugger, frame))
            {
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

    private static EnvDTE.Thread RequireCurrentThread(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return debugger.CurrentThread
            ?? throw new InvalidOperationException("The debugger has no current thread; the session may have ended.");
    }

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
            }
            catch (InvalidComObjectException)
            {
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

            await Task.Delay(_debuggerPollMs, cancellationToken);
        }
    }

    private static JObject DescribeDebugger(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        dbgDebugMode mode;
        try
        {
            mode = debugger.CurrentMode;
        }
        catch (COMException)
        {
            mode = dbgDebugMode.dbgDesignMode;
        }
        catch (InvalidComObjectException)
        {
            mode = dbgDebugMode.dbgDesignMode;
        }

        return DescribeDebugger(debugger, mode, timedOut: null);
    }

    private static JObject DescribeDebugger(Debugger debugger, dbgDebugMode observedMode, bool? timedOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var result = new JObject
        {
            ["mode"] = VsDebuggerChannelRules.ModeWireName(ToMode(observedMode)),
            ["processes"] = DebuggedProcessesToJson(debugger),
        };

        if (observedMode == dbgDebugMode.dbgBreakMode)
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
            }
            catch (InvalidComObjectException)
            {
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
        }
        catch (InvalidComObjectException)
        {
        }

        return processes;
    }

    private static JObject FrameToJson(StackFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var json = new JObject();
        try
        {
            json["function"] = frame.FunctionName;
            json["module"] = frame.Module;
            json["language"] = frame.Language;
        }
        catch (COMException)
        {
        }

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

    private static int ReadWaitMs(JObject args, string propertyName, int defaultMs, int minMs = 0)
    {
        return VsDebuggerChannelRules.ClampWaitMs(args[propertyName]?.Value<int?>(), defaultMs, minMs, _maxWaitMs);
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
