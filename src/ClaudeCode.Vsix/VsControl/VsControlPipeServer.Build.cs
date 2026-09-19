using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;
using OutputWindowPane = EnvDTE.OutputWindowPane;

namespace ClaudeCode.Vsix.VsControl;

/// <summary>Build, Error List and Output window methods of the VS control channel.</summary>
internal sealed partial class VsControlPipeServer
{
    private const int _defaultOutputChars = 20_000;
    private const int _maxOutputChars = 200_000;

    /// <summary>
    /// Builds, rebuilds or cleans the current solution and waits for completion.
    /// <c>errorCount</c>/<c>warningCount</c> reflect the Error List window's contents right after the
    /// build - which are themselves subject to the Error List's own Build/IntelliSense scope filters -
    /// not a raw MSBuild diagnostic count. The VS SDK does not expose MSBuild's own diagnostic totals
    /// without driving <c>IVsSolutionBuildManager</c> directly; the Error List is the diagnostic surface
    /// <see cref="Community.VisualStudio.Toolkit"/> already gives us.
    /// </summary>
    private static async Task<JObject> BuildSolutionAsync(JObject args)
    {
        // The optional `configuration` param only takes effect if it matches an existing solution
        // configuration name; a mismatched or omitted value simply builds whatever is active.
        var configurationName = args["configuration"]?.Value<string>();
        if (!string.IsNullOrEmpty(configurationName))
        {
            await TrySetActiveConfigurationAsync(configurationName!);
        }

        var action = ParseBuildAction(args);
        var succeeded = await VS.Build.BuildSolutionAsync(action);
        return await DescribeBuildResultAsync(succeeded, action);
    }

    private static async Task<JObject> BuildProjectAsync(JObject args)
    {
        var projectName = RequireString(args, "projectName");
        var project = await FindProjectAsync(projectName);
        var action = ParseBuildAction(args);
        var succeeded = await VS.Build.BuildProjectAsync(project, action);
        var result = await DescribeBuildResultAsync(succeeded, action);
        result["project"] = project.Name;
        return result;
    }

    private static async Task<JObject> DescribeBuildResultAsync(bool succeeded, BuildAction action)
    {
        var (errorCount, warningCount) = await CountBuildDiagnosticsAsync();
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
        var maxChars = Math.Min(_maxOutputChars, Math.Max(1, args["maxChars"]?.Value<int?>() ?? _defaultOutputChars));

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

        var document = pane.TextDocument;
        var text = document.StartPoint.CreateEditPoint().GetText(document.EndPoint) ?? string.Empty;
        var truncated = text.Length > maxChars;
        if (truncated)
        {
            text = text.Substring(text.Length - maxChars);
        }

        return new JObject
        {
            ["pane"] = pane.Name,
            ["text"] = text,
            ["truncated"] = truncated,
            ["totalChars"] = document.EndPoint.AbsoluteCharOffset,
        };
    }
}
