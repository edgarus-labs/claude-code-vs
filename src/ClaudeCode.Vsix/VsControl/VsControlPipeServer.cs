using ClaudeCode.Contracts;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Tagging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.VsControl;

internal sealed partial class VsControlPipeServer : IAsyncDisposable
{
    private static readonly JsonSerializerSettings _envelopeSettings = new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    };

    private const int _rpcServerCallRetryLaterHResult = unchecked((int)0x8001010A);

    private static readonly HashSet<string> _allowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Edit.FormatDocument",
        "Edit.FormatSelection",
        "Debug.StopDebugging",
        "File.SaveAll",
        "View.ErrorList",
    };

    private static readonly TimeSpan _handshakeTimeout = TimeSpan.FromSeconds(5);

    private const int _maxHandshakeLineChars = 512;

    private readonly string _pipeName;
    private readonly string? _workspaceRoot;
    private readonly string _token;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Microsoft.VisualStudio.Threading.JoinableTask? _listenTask;
    private volatile NamedPipeServerStream? _pipe;

    public VsControlPipeServer(string pipeName, string? workspaceRoot, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("A non-empty handshake token is required; an empty token would authenticate any local caller.", nameof(token));
        }

        _pipeName = pipeName;
        _workspaceRoot = workspaceRoot;
        _token = token;
    }

    public void Start()
    {
#pragma warning disable VSSDK007
        _listenTask = ThreadHelper.JoinableTaskFactory.RunAsync(() => RunAsync(_cts.Token));
#pragma warning restore VSSDK007
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            CreatePipeSecurity());
        _pipe = pipe;

        while (!cancellationToken.IsCancellationRequested)
        {
            bool connected = false;
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                connected = true;

                var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using var reader = new StreamReader(pipe, utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, utf8NoBom, bufferSize: 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

                if (!await TryHandshakeAsync(reader, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var responseLine = await HandleRequestLineAsync(line, cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(responseLine).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            finally
            {
                if (connected && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        pipe.Disconnect();
                    }
                    catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                }
            }
        }
    }

    private async Task<bool> TryHandshakeAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_handshakeTimeout);

        string? tokenLine;
        try
        {
            var readTask = PipeHandshakeLineReader.ReadBoundedLineAsync(reader, _maxHandshakeLineChars);
            var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
            if (await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false) != readTask)
            {
                _ = readTask.ContinueWith(task => { _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return false;
            }

            tokenLine = await readTask.ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }

        return tokenLine is not null && ConstantTimeTokenComparer.Equals(tokenLine, _token);
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        return PipeSecurityFactory.CreateCurrentUserOnly(PipeAccessRights.ReadWrite);
    }

    private async Task<string> HandleRequestLineAsync(string line, CancellationToken cancellationToken)
    {
        VsControlRequest request;
        try
        {
            request = JsonConvert.DeserializeObject<VsControlRequest>(line, _envelopeSettings) ?? new VsControlRequest();
        }
        catch (JsonException ex)
        {
            return JsonConvert.SerializeObject(new VsControlResponse { Id = string.Empty, Error = $"Malformed request: {ex.Message}" }, _envelopeSettings);
        }

        VsControlResponse response;
        try
        {
            var resultJson = await DispatchAsync(request.Method, request.ParamsJson, cancellationToken).ConfigureAwait(false);
            response = new VsControlResponse { Id = request.Id, ResultJson = resultJson };
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            response = new VsControlResponse { Id = request.Id, Error = ex.Message };
        }

        return JsonConvert.SerializeObject(response, _envelopeSettings);
    }

    private async Task<string> DispatchAsync(string method, string paramsJson, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var args = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);

        switch (method)
        {
            case "listOpenDocuments": return (await ListOpenDocumentsAsync()).ToString(Formatting.None);
            case "openDocument": return (await OpenDocumentAsync(args)).ToString(Formatting.None);
            case "getActiveDocument": return (await GetActiveDocumentAsync()).ToString(Formatting.None);
            case "getSelection": return (await GetSelectionAsync()).ToString(Formatting.None);
            case "replaceSelection": return (await ReplaceSelectionAsync(args)).ToString(Formatting.None);
            case "saveAll": return (await SaveAllAsync()).ToString(Formatting.None);
            case "buildSolution": return (await BuildSolutionAsync(args, cancellationToken)).ToString(Formatting.None);
            case "buildProject": return (await BuildProjectAsync(args, cancellationToken)).ToString(Formatting.None);
            case "getBuildErrors": return (await GetBuildErrorsAsync(args)).ToString(Formatting.None);
            case "getOutput": return (await GetOutputAsync(args)).ToString(Formatting.None);
            case "getDiagnostics": return (await GetDiagnosticsAsync(args)).ToString(Formatting.None);
            case "runCommand": return (await RunCommandAsync(args)).ToString(Formatting.None);
            case "getSolutionInfo": return (await GetSolutionInfoAsync()).ToString(Formatting.None);
            case "addFileToProject": return (await AddFileToProjectAsync(args)).ToString(Formatting.None);
            case "addProjectToSolution": return (await AddProjectToSolutionAsync(args)).ToString(Formatting.None);
            case "openSolution": return (await OpenSolutionAsync(args)).ToString(Formatting.None);
            case "startDebugging": return (await StartDebuggingAsync(args, cancellationToken)).ToString(Formatting.None);
            case "stopDebugging": return (await StopDebuggingAsync(cancellationToken)).ToString(Formatting.None);
            case "getDebuggerState": return (await GetDebuggerStateAsync()).ToString(Formatting.None);
            case "setBreakpoint": return (await SetBreakpointAsync(args)).ToString(Formatting.None);
            case "removeBreakpoint": return (await RemoveBreakpointAsync(args)).ToString(Formatting.None);
            case "listBreakpoints": return (await ListBreakpointsAsync()).ToString(Formatting.None);
            case "continueDebugging": return (await StepAsync(args, DebuggerStep.Continue, cancellationToken)).ToString(Formatting.None);
            case "stepOver": return (await StepAsync(args, DebuggerStep.Over, cancellationToken)).ToString(Formatting.None);
            case "stepInto": return (await StepAsync(args, DebuggerStep.Into, cancellationToken)).ToString(Formatting.None);
            case "stepOut": return (await StepAsync(args, DebuggerStep.Out, cancellationToken)).ToString(Formatting.None);
            case "waitForBreak": return (await WaitForBreakAsync(args, cancellationToken)).ToString(Formatting.None);
            case "getCallStack": return (await GetCallStackAsync()).ToString(Formatting.None);
            case "getLocals": return (await GetLocalsAsync(args)).ToString(Formatting.None);
            case "evaluateExpression": return (await EvaluateExpressionAsync(args)).ToString(Formatting.None);
            case "listAppWindows": return (await ListAppWindowsAsync(cancellationToken)).ToString(Formatting.None);
            case "getWindowElements": return (await GetWindowElementsAsync(args, cancellationToken)).ToString(Formatting.None);
            case "invokeElement": return (await InvokeElementAsync(args, cancellationToken)).ToString(Formatting.None);
            case "setElementValue": return (await SetElementValueAsync(args, cancellationToken)).ToString(Formatting.None);
            case "captureWindow": return (await CaptureWindowAsync(args, cancellationToken)).ToString(Formatting.None);
            default: throw new InvalidOperationException($"Unknown VsControl method '{method}'.");
        }
    }

    private static async Task<JObject> ListOpenDocumentsAsync()
    {
        var frames = await VS.Windows.GetAllDocumentWindowsAsync();
        var active = await VS.Documents.GetActiveDocumentViewAsync();

        var documents = new JArray();
        foreach (var frame in frames)
        {
            var view = await frame.GetDocumentViewAsync();
            if (view?.FilePath is null)
            {
                continue;
            }

            documents.Add(new JObject
            {
                ["path"] = view.FilePath,
                ["isDirty"] = view.Document?.IsDirty ?? false,
                ["isActive"] = string.Equals(view.FilePath, active?.FilePath, StringComparison.OrdinalIgnoreCase),
            });
        }

        return new JObject { ["documents"] = documents };
    }

    private async Task<JObject> OpenDocumentAsync(JObject args)
    {
        var path = RequireString(args, "path");
        using var pathLease = WorkspacePathGuard.AcquireDocument(_workspaceRoot, path);
        var fullPath = pathLease.FullPath;

        var view = await VS.Documents.OpenAsync(fullPath);
        if (view is null)
        {
            throw new InvalidOperationException($"'{path}' could not be opened.");
        }

        var line = args["line"]?.Value<int?>();
        if (line.HasValue && view.TextView is not null && view.TextBuffer is not null)
        {
            EditorCaret.MoveToLine(view.TextView, view.TextBuffer, line.Value);
        }

        return new JObject();
    }

    private static async Task<JToken> GetActiveDocumentAsync()
    {
        var view = await VS.Documents.GetActiveDocumentViewAsync();
        if (view?.FilePath is null || view.TextBuffer is null)
        {
            return JValue.CreateNull();
        }

        var snapshot = view.TextBuffer.CurrentSnapshot;
        var selection = view.TextView?.Selection;
        int selectionStart, selectionEnd;
        if (selection is not null && !selection.IsEmpty)
        {
            var span = selection.StreamSelectionSpan.SnapshotSpan;
            selectionStart = span.Start.Position;
            selectionEnd = span.End.Position;
        }
        else
        {
            var caretPosition = view.TextView?.Caret.Position.BufferPosition.Position ?? 0;
            selectionStart = caretPosition;
            selectionEnd = caretPosition;
        }

        return new JObject
        {
            ["path"] = view.FilePath,
            ["text"] = snapshot.GetText(),
            ["selectionStart"] = selectionStart,
            ["selectionEnd"] = selectionEnd,
        };
    }

    private static async Task<JToken> GetSelectionAsync()
    {
        var view = await VS.Documents.GetActiveDocumentViewAsync();
        if (view?.FilePath is null || view.TextView is null)
        {
            return JValue.CreateNull();
        }

        var span = view.TextView.Selection.StreamSelectionSpan.SnapshotSpan;
        var startLine = span.Start.GetContainingLine().LineNumber + 1;
        var endLine = span.End.GetContainingLine().LineNumber + 1;

        return new JObject
        {
            ["path"] = view.FilePath,
            ["text"] = span.GetText(),
            ["startLine"] = startLine,
            ["endLine"] = endLine,
        };
    }

    private async Task<JObject> ReplaceSelectionAsync(JObject args)
    {
        var path = RequireString(args, "path");
        var text = RequireString(args, "text");
        using var pathLease = WorkspacePathGuard.AcquireDocument(_workspaceRoot, path);
        var fullPath = pathLease.FullPath;

        var view = await VS.Documents.GetDocumentViewAsync(fullPath) ?? await VS.Documents.OpenAsync(fullPath);
        if (view?.TextView is null || view.TextBuffer is null)
        {
            throw new InvalidOperationException($"'{path}' could not be opened.");
        }

        var span = view.TextView.Selection.StreamSelectionSpan.SnapshotSpan;
        var edit = view.TextBuffer.CreateEdit();
        bool rejected;
        try
        {
            if (!edit.Replace(span.Span, text) || edit.HasFailedChanges)
            {
                rejected = true;
            }
            else
            {
                edit.Apply();
                rejected = edit.HasFailedChanges || edit.Canceled;
            }
        }
        finally
        {
            edit.Dispose();
        }

        if (rejected)
        {
            throw new InvalidOperationException("The edit was rejected (read-only buffer or vetoed by another extension).");
        }

        return new JObject();
    }

    private static async Task<JObject> SaveAllAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE>();
        dte.ExecuteCommand("File.SaveAll");

        return new JObject();
    }

    private static async Task<string?> TrySetActiveConfigurationAsync(string? configurationName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE>();
        var solutionBuild = dte.Solution?.SolutionBuild;
        if (solutionBuild is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(configurationName))
        {
            foreach (SolutionConfiguration configuration in solutionBuild.SolutionConfigurations)
            {
                if (string.Equals(configuration.Name, configurationName, StringComparison.OrdinalIgnoreCase))
                {
                    configuration.Activate();

                    break;
                }
            }
        }

        try
        {
            return solutionBuild.ActiveConfiguration?.Name;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private async Task<JObject> GetDiagnosticsAsync(JObject args)
    {
        var path = RequireString(args, "path");
        using var pathLease = WorkspacePathGuard.AcquireDocument(_workspaceRoot, path);
        var fullPath = pathLease.FullPath;

        var view = await VS.Documents.GetDocumentViewAsync(fullPath) ?? await VS.Documents.OpenAsync(fullPath);
        if (view?.TextView is null || view.TextBuffer is null)
        {
            throw new InvalidOperationException($"'{path}' could not be opened.");
        }

        var diagnostics = new JArray();
        var tagAggregatorFactory = await VS.GetMefServiceAsync<IViewTagAggregatorFactoryService>();
        using var aggregator = tagAggregatorFactory.CreateTagAggregator<IErrorTag>(view.TextView);

        var fullSpan = new Microsoft.VisualStudio.Text.SnapshotSpan(view.TextBuffer.CurrentSnapshot, 0, view.TextBuffer.CurrentSnapshot.Length);
        foreach (var mappedTag in aggregator.GetTags(fullSpan))
        {
            var spans = mappedTag.Span.GetSpans(view.TextBuffer);
            if (spans.Count == 0)
            {
                continue;
            }

            var start = spans[0].Start;
            diagnostics.Add(new JObject
            {
                ["line"] = start.GetContainingLine().LineNumber + 1,
                ["column"] = start.Position - start.GetContainingLine().Start.Position + 1,
                ["message"] = mappedTag.Tag.ToolTipContent?.ToString() ?? string.Empty,
                ["severity"] = ErrorTypeToSeverity(mappedTag.Tag.ErrorType),
                ["source"] = "language-service",
            });
        }

        return new JObject { ["diagnostics"] = diagnostics };
    }

    private static async Task<JObject> RunCommandAsync(JObject args)
    {
        var commandName = RequireString(args, "commandName");
        if (!_allowedCommands.Contains(commandName))
        {
            throw new InvalidOperationException($"Command '{commandName}' is not allow-listed for VS control.");
        }

        var commandArgs = args["args"]?.Value<string>() ?? string.Empty;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE>();
        try
        {
            dte.ExecuteCommand(commandName, commandArgs);
        }
        catch (COMException ex)
        {
            throw ActionableDteError(ex, $"Command '{commandName}' could not be executed.");
        }

        return new JObject();
    }

    private static InvalidOperationException ActionableDteError(COMException error, string failureMessage) =>
        error.HResult == _rpcServerCallRetryLaterHResult
            ? new InvalidOperationException("Visual Studio is busy, try again.")
            : new InvalidOperationException(failureMessage);

    private async Task<JObject> AddFileToProjectAsync(JObject args)
    {
        var projectName = RequireString(args, "projectName");
        var path = RequireString(args, "path");
        using var pathLease = WorkspacePathGuard.AcquireDocument(_workspaceRoot, path);
        var fullPath = pathLease.FullPath;
        var project = await FindProjectAsync(projectName);
        await project.AddExistingFilesAsync(fullPath);
        return new JObject { ["project"] = project.Name, ["path"] = fullPath };
    }

    private async Task<JObject> AddProjectToSolutionAsync(JObject args)
    {
        var path = RequireString(args, "path");
        using var pathLease = WorkspacePathGuard.AcquireDocument(_workspaceRoot, path);
        var fullPath = pathLease.FullPath;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<EnvDTE.DTE, EnvDTE80.DTE2>();
        var solution = dte.Solution;
        if (solution is null || !solution.IsOpen)
        {
            throw new InvalidOperationException("No solution is open.");
        }

        EnvDTE.Project? project;
        try
        {
            project = solution.AddFromFile(fullPath, Exclusive: false);
        }
        catch (COMException ex)
        {
            throw ActionableDteError(ex, $"'{path}' could not be added to the solution; check that it is a project type this Visual Studio can load and that no dialog is open.");
        }

        return new JObject { ["name"] = project?.Name, ["path"] = fullPath };
    }

    private async Task<JObject> OpenSolutionAsync(JObject args)
    {
        var path = RequireString(args, "path");
        using var pathLease = WorkspacePathGuard.AcquireDocument(_workspaceRoot, path);
        var fullPath = pathLease.FullPath;
        var extension = Path.GetExtension(fullPath);
        if (!string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{path}' is not a solution file (.sln/.slnx).");
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE2>();
        try
        {
            if (dte.Solution.IsOpen)
            {
                dte.Solution.Close(SaveFirst: true);
            }

            dte.Solution.Open(fullPath);
        }
        catch (COMException ex)
        {
            throw ActionableDteError(ex, $"'{path}' could not be opened; check that it is a solution this Visual Studio can load and that no dialog is open.");
        }

        return await GetSolutionInfoAsync();
    }

    private static async Task<JObject> GetSolutionInfoAsync()
    {
        var solution = await VS.Solutions.GetCurrentSolutionAsync();
        if (solution is null)
        {
            return new JObject { ["solutionPath"] = null, ["projects"] = new JArray() };
        }

        var projects = new JArray();
        foreach (var project in await VS.Solutions.GetAllProjectsAsync())
        {
            projects.Add(new JObject
            {
                ["name"] = project.Name,
                ["path"] = project.FullPath,
            });
        }

        return new JObject
        {
            ["solutionPath"] = solution.FullPath,
            ["projects"] = projects,
        };
    }

    private static async Task<IReadOnlyList<ErrorItem>> GetErrorListItemsAsync()
    {
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE2>();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var errorItems = dte.ToolWindows.ErrorList.ErrorItems;
        var results = new List<ErrorItem>(errorItems.Count);
        for (var i = 1; i <= errorItems.Count; i++)
        {
            results.Add(errorItems.Item(i));
        }

        return results;
    }

    private static async Task<(int ErrorCount, int WarningCount)> CountBuildDiagnosticsAsync(string? projectName = null)
    {
        var items = await GetErrorListItemsAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var errorCount = 0;
        var warningCount = 0;
        foreach (var item in items)
        {
            if (projectName is not null && !VsBuildChannelRules.MatchesProject(item.Project, projectName))
            {
                continue;
            }

            if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
            {
                errorCount++;
            }
            else if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium)
            {
                warningCount++;
            }
        }

        return (errorCount, warningCount);
    }

    private static string ErrorLevelToSeverity(vsBuildErrorLevel level) => level switch
    {
        vsBuildErrorLevel.vsBuildErrorLevelHigh => "error",
        vsBuildErrorLevel.vsBuildErrorLevelMedium => "warning",
        _ => "message",
    };

    private static string ErrorTypeToSeverity(string errorType) =>
        errorType.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0 ? "warning" : "error";

    private static string RequireString(JObject args, string propertyName)
    {
        var value = args[propertyName]?.Value<string>();
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"Missing required parameter '{propertyName}'.");
        }

        return value!;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _pipe?.Dispose();

        try
        {
            if (_listenTask is not null)
            {
                await _listenTask.JoinAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
