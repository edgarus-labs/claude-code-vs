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

internal sealed partial class VsControlPipeServer
{
    private const int _defaultOutputChars = 20_000;
    private const int _maxOutputChars = 200_000;
    private const int _maxBuildErrorRows = 1_000;

    private static readonly TimeSpan _buildTimeout = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> _errorSeverities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "error",
        "warning",
        "message",
    };

    private static async Task<JObject> BuildSolutionAsync(JObject args, CancellationToken cancellationToken)
    {
        var action = ParseBuildAction(args);

        var activeConfiguration = await TrySetActiveConfigurationAsync(args["configuration"]?.Value<string>());

        var succeeded = await RunBuildAsync(() => VS.Build.BuildSolutionAsync(action), cancellationToken);
        var result = await DescribeBuildResultAsync(succeeded, action);
        result["configuration"] = activeConfiguration;
        return result;
    }

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

    private static async Task<bool> RunBuildAsync(Func<Task<bool>> startBuild, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_buildTimeout);

        var build = startBuild();
        var expiry = Task.Delay(Timeout.Infinite, budget.Token);
        if (await Task.WhenAny(build, expiry) != build)
        {
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
            throw new InvalidOperationException("The build was cancelled in Visual Studio.");
        }
        catch (COMException)
        {
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
            throw new InvalidOperationException($"Unknown severity '{severityFilter}'; use error, warning or message.");
        }

        var items = await GetErrorListItemsAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var errors = new JArray();
        var truncated = false;
        foreach (var item in items)
        {
            var severity = ErrorLevelToSeverity(item.ErrorLevel);
            if (!string.IsNullOrEmpty(severityFilter) && !string.Equals(severity, severityFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (errors.Count >= _maxBuildErrorRows)
            {
                truncated = true;
                break;
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

        return new JObject { ["errors"] = errors, ["truncated"] = truncated };
    }

    private static async Task<JObject> GetOutputAsync(JObject args)
    {
        var paneName = args["pane"]?.Value<string>();
        if (string.IsNullOrEmpty(paneName)) paneName = "Debug";
        var clear = args["clear"]?.Value<bool?>() ?? false;
        var maxChars = VsBuildChannelRules.ClampOutputChars(args["maxChars"]?.Value<int?>(), _defaultOutputChars, _maxOutputChars);

        var wellKnownGuid = VsBuildChannelRules.WellKnownOutputPaneGuid(paneName!);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE2>();
        var panes = dte.ToolWindows.OutputWindow.OutputWindowPanes;
        OutputWindowPane? pane = null;
        var available = new JArray();
        foreach (OutputWindowPane candidate in panes)
        {
            available.Add(candidate.Name);
            if (string.Equals(candidate.Name, paneName, StringComparison.OrdinalIgnoreCase)
                || (wellKnownGuid.HasValue && Guid.TryParse(candidate.Guid, out var candidateGuid) && candidateGuid == wellKnownGuid.Value))
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
        var text = VsBuildChannelRules.TakeOutputTail(raw, maxChars);
        truncated |= text.Length < raw.Length;

        return new JObject
        {
            ["pane"] = pane.Name,
            ["text"] = text,
            ["truncated"] = truncated,
            ["totalChars"] = end.AbsoluteCharOffset - start.AbsoluteCharOffset,
        };
    }
}
