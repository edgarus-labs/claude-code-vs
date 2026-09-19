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
using OutputWindowPane = EnvDTE.OutputWindowPane;

namespace ClaudeCode.Vsix.VsControl;

/// <summary>Build, Error List and Output window methods of the VS control channel.</summary>
internal sealed partial class VsControlPipeServer
{
    private const int _defaultOutputChars = 20_000;
    private const int _maxOutputChars = 200_000;

    // The server-side backstop that keeps a build Visual Studio never reports completion for from
    // wedging the single sequential request loop for the rest of the session. It sits deliberately
    // BELOW the MCP client's own 12-minute build budget (VsControlPipeClient._buildTimeout), so the
    // actionable "did not report completion" error below reaches the agent instead of the client
    // inventing a transport timeout. Raising it above that budget reinstates exactly that failure.
    private static readonly TimeSpan _buildTimeout = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> _errorSeverities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "error",
        "warning",
        "message",
    };

    /// <summary>
    /// Builds, rebuilds or cleans the current solution and waits for completion.
    /// <c>errorCount</c>/<c>warningCount</c> reflect the Error List window's contents right after the
    /// build - which are themselves subject to the Error List's own Build/IntelliSense scope filters -
    /// not a raw MSBuild diagnostic count. The VS SDK does not expose MSBuild's own diagnostic totals
    /// without driving <c>IVsSolutionBuildManager</c> directly; the Error List is the diagnostic surface
    /// <see cref="Community.VisualStudio.Toolkit"/> already gives us. <c>configuration</c> reports the
    /// solution configuration the build actually ran in, which is not necessarily the requested one.
    /// </summary>
    private static async Task<JObject> BuildSolutionAsync(JObject args, CancellationToken cancellationToken)
    {
        // Validate before mutating: an unknown action must not leave the user's active solution
        // configuration switched to something they never selected by a request we then reject.
        var action = ParseBuildAction(args);

        // The optional `configuration` param only takes effect if it matches an existing solution
        // configuration name; a mismatched or omitted value simply builds whatever is active. The
        // configuration actually used goes into the result either way, so a substitution is visible
        // to the agent instead of reading as a successful build of what it asked for.
        var activeConfiguration = await TrySetActiveConfigurationAsync(args["configuration"]?.Value<string>());

        var succeeded = await RunBuildAsync(() => VS.Build.BuildSolutionAsync(action), cancellationToken);
        var result = await DescribeBuildResultAsync(succeeded, action);
        result["configuration"] = activeConfiguration;
        return result;
    }

    /// <summary>Builds, rebuilds or cleans one loaded project. <c>errorCount</c>/<c>warningCount</c>
    /// carry the same Error List caveat as <see cref="BuildSolutionAsync"/>, narrowed to this
    /// project's own items so an unrelated broken project is not reported as this one's failure.</summary>
    private static async Task<JObject> BuildProjectAsync(JObject args, CancellationToken cancellationToken)
    {
        var projectName = RequireString(args, "projectName");
        var project = await FindProjectAsync(projectName);
        var action = ParseBuildAction(args);
        var succeeded = await RunBuildAsync(() => VS.Build.BuildProjectAsync(project, action), cancellationToken);
        var result = await DescribeBuildResultAsync(succeeded, action, project.Name);
        result["project"] = project.Name;
        return result;
    }

    /// <summary>
    /// Awaits one toolkit build under a bound and turns its two non-success exits into errors the
    /// agent can act on. The toolkit signals completion from an <c>IVsUpdateSolutionEvents</c> sink,
    /// so a build whose project Visual Studio never attempts - a dependency failed and it was
    /// skipped - never signals at all. Requests are processed strictly one at a time, so an
    /// unbounded await here would wedge the whole control channel for the rest of the session and
    /// block disposal on the listen loop.
    /// </summary>
    private static async Task<bool> RunBuildAsync(Func<Task<bool>> startBuild, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_buildTimeout);

        var build = startBuild();
        var expiry = Task.Delay(Timeout.Infinite, budget.Token);
        if (await Task.WhenAny(build, expiry) != build)
        {
            // Observe the abandoned build so a later failure cannot surface as an unobserved task
            // exception.
            _ = build.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"The build did not report completion within {_buildTimeout.TotalMinutes:0} minutes; Visual Studio may have skipped it because a dependency failed. Check the Build output pane.");
        }

        try
        {
            return await build;
        }
        catch (OperationCanceledException)
        {
            // The toolkit's solution-events sink cancels its completion source when the build is
            // cancelled in the IDE. Raw "A task was canceled." is indistinguishable from a transport
            // failure and invites the agent to restart a build the user just stopped.
            throw new InvalidOperationException("The build was cancelled in Visual Studio.");
        }
        catch (COMException)
        {
            // StartSimpleUpdateSolutionConfiguration refuses with a bare HRESULT while another build
            // is in flight; docs/VsControlProtocol.md promises an actionable error for that case.
            throw new InvalidOperationException("Visual Studio could not start the build; another build may already be running.");
        }
    }

    private static async Task<JObject> DescribeBuildResultAsync(bool succeeded, BuildAction action, string? projectName = null)
    {
        var (errorCount, warningCount) = await CountBuildDiagnosticsAsync(projectName);
        return new JObject
        {
            ["action"] = action.ToString().ToLowerInvariant(),
            ["succeeded"] = succeeded,
            ["errorCount"] = errorCount,
            ["warningCount"] = warningCount,
        };
    }

    private static BuildAction ParseBuildAction(JObject args)
    {
        var action = args["action"]?.Value<string>();
        if (string.IsNullOrEmpty(action) || string.Equals(action, "build", StringComparison.OrdinalIgnoreCase)) return BuildAction.Build;
        if (string.Equals(action, "rebuild", StringComparison.OrdinalIgnoreCase)) return BuildAction.Rebuild;
        if (string.Equals(action, "clean", StringComparison.OrdinalIgnoreCase)) return BuildAction.Clean;
        throw new InvalidOperationException($"Unknown build action '{action}'; use build, rebuild or clean.");
    }

    private static async Task<Community.VisualStudio.Toolkit.Project> FindProjectAsync(string projectName)
    {
        foreach (var candidate in await VS.Solutions.GetAllProjectsAsync())
        {
            if (string.Equals(candidate.Name, projectName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"No project named '{projectName}' is loaded in the solution.");
    }

    private static async Task<JObject> GetBuildErrorsAsync(JObject args)
    {
        var severityFilter = args["severity"]?.Value<string>();
        if (!string.IsNullOrEmpty(severityFilter) && !_errorSeverities.Contains(severityFilter!))
        {
            // An empty list is the same answer as "the build is clean", so a typo'd filter must not
            // read to the agent as success.
            throw new InvalidOperationException($"Unknown severity '{severityFilter}'; use error, warning or message.");
        }

        var items = await GetErrorListItemsAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var errors = new JArray();
        foreach (var item in items)
        {
            var severity = ErrorLevelToSeverity(item.ErrorLevel);
            if (!string.IsNullOrEmpty(severityFilter) && !string.Equals(severity, severityFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            errors.Add(new JObject
            {
                ["file"] = item.FileName,
                ["line"] = item.Line,
                ["column"] = item.Column,
                ["message"] = item.Description,
                ["project"] = item.Project,
                ["severity"] = severity,
            });
        }

        return new JObject { ["errors"] = errors };
    }

    /// <summary>Reads (or clears) one Output window pane. The Debug pane carries the debugged app's
    /// Debug/Trace/Console output, the Build pane MSBuild's full log.</summary>
    private static async Task<JObject> GetOutputAsync(JObject args)
    {
        var paneName = args["pane"]?.Value<string>();
        if (string.IsNullOrEmpty(paneName)) paneName = "Debug";
        var clear = args["clear"]?.Value<bool?>() ?? false;
        var maxChars = VsBuildChannelRules.ClampOutputChars(args["maxChars"]?.Value<int?>(), _defaultOutputChars, _maxOutputChars);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE2>();
        var panes = dte.ToolWindows.OutputWindow.OutputWindowPanes;
        OutputWindowPane? pane = null;
        var available = new JArray();
        foreach (OutputWindowPane candidate in panes)
        {
            available.Add(candidate.Name);
            if (string.Equals(candidate.Name, paneName, StringComparison.OrdinalIgnoreCase))
            {
                pane = candidate;
            }
        }

        if (pane is null)
        {
            throw new InvalidOperationException($"No Output pane named '{paneName}'. Available: {string.Join(", ", available)}.");
        }

        if (clear)
        {
            pane.Clear();
            return new JObject { ["pane"] = pane.Name, ["cleared"] = true };
        }

        // Only the requested tail crosses the COM boundary. A long-running Build/Debug pane holds tens
        // of megabytes; marshalling all of it into one managed string to keep the last few thousand
        // characters stalls the UI thread and churns the large object heap.
        var document = pane.TextDocument;
        var end = document.EndPoint;
        var start = document.StartPoint;
        var truncated = (end.AbsoluteCharOffset - start.AbsoluteCharOffset) > maxChars;
        var cursor = truncated ? end.CreateEditPoint() : start.CreateEditPoint();
        if (truncated)
        {
            cursor.CharLeft(maxChars);
        }

        var raw = cursor.GetText(end) ?? string.Empty;
        // A line break counts as one character for CharLeft but two in the text it returns, so the
        // slice can still overshoot the cap.
        var text = VsBuildChannelRules.TakeOutputTail(raw, maxChars);
        truncated |= text.Length < raw.Length;

        return new JObject
        {
            ["pane"] = pane.Name,
            ["text"] = text,
            ["truncated"] = truncated,
            // A character count, not EndPoint.AbsoluteCharOffset: that offset is 1-based, and the
            // truncation decision above already measures the pane by the same difference.
            ["totalChars"] = end.AbsoluteCharOffset - start.AbsoluteCharOffset,
        };
    }
}
