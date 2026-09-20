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

    // RPC_E_SERVERCALL_RETRYLATER: the VS main-thread COM message pump momentarily refused this call
    // (e.g. it is mid another automation call or a modal dialog is up); not a real failure.
    private const int _rpcServerCallRetryLaterHResult = unchecked((int)0x8001010A);

    // This allow-list is what keeps `runCommand` from becoming a generic "execute any DTE command by
    // name" surface for an ACP agent (or anything impersonating one over this pipe): adding an entry
    // here adds a capability. It is deliberately NOT a claim that this channel cannot execute code
    // from the open solution - build and debug execution are exposed through the dedicated,
    // individually documented `buildSolution`, `buildProject` and `startDebugging` methods below
    // (see docs/VsControlProtocol.md), so the absence of Build.* / Debug.Start command names here
    // does not remove that capability.
    private static readonly HashSet<string> _allowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Edit.FormatDocument",
        "Edit.FormatSelection",
        "Debug.StopDebugging",
        "File.SaveAll",
        "View.ErrorList",
    };

    // Matches the client's 5s connect timeout: a peer that has not sent its token by then is not a
    // legitimate client and must not keep the single retained pipe instance occupied.
    private static readonly TimeSpan _handshakeTimeout = TimeSpan.FromSeconds(5);

    // The handshake token is a 32-byte RNG value base64-encoded by VsControlSessionRegistry - 44
    // characters - so no longer line can ever authenticate. Capping the read keeps an
    // unauthenticated peer from streaming newline-free bytes into this process for the whole window.
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
        // VSSDK007 does not see across methods: _listenTask is captured here and joined in
        // DisposeAsync (below) as part of the shutdown sequence. Start() intentionally returns
        // immediately; this is a tracked, not fire-and-forget, background listen loop.
#pragma warning disable VSSDK007
        _listenTask = ThreadHelper.JoinableTaskFactory.RunAsync(() => RunAsync(_cts.Token));
#pragma warning restore VSSDK007
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Keep the sole server instance alive across reconnects. Recreating it would release the
        // pipe name between clients, allowing another process to occupy that name.
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
                    // Missing/wrong token: another process on this machine (permitted by the pipe ACL
                    // because it runs as the same Windows user) guessed the pipe name. Drop the
                    // connection without processing any request and wait for the next one, instead of
                    // tearing down the whole listener.
                    continue;
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break; // client disconnected
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
                break; // Session ended; expected on dispose.
            }
            catch (IOException)
            {
                // Pipe broken or closed by the client mid-request; expected when the Mcp server
                // process exits abruptly. Loop back and accept the next connection instead of ending
                // the listener.
            }
            catch (ObjectDisposedException)
            {
                // Pipe disposed concurrently with a pending read/write; expected on dispose.
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
                        // Session disposal closed the retained pipe while this client exited.
                    }
                }
            }
        }
    }

    private async Task<bool> TryHandshakeAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        // The sole server instance is retained across reconnects, so a peer that connects and then
        // stays silent would hold the only listener forever. Bound the handshake read twice over: by
        // time (here) and by length (PipeHandshakeLineReader), because the pipe name is enumerable by
        // any process running as this Windows user. On timeout or shutdown the caller drops the
        // connection and goes back to accepting.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_handshakeTimeout);

        string? tokenLine;
        try
        {
            var readTask = PipeHandshakeLineReader.ReadBoundedLineAsync(reader, _maxHandshakeLineChars);
            var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
            if (await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false) != readTask)
            {
                // Observe the abandoned read so tearing the pipe down under it cannot surface as an
                // unobserved task exception.
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
            // A read-only region or a conflicting edit makes Replace return false and sets
            // HasFailedChanges without ever setting Canceled, so checking Canceled alone would
            // report success for a replacement that never landed.
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

    /// <summary>
    /// Activates the solution configuration named <paramref name="configurationName"/> if one
    /// exists, and returns the configuration that is active afterwards. Visual Studio matches on
    /// <c>SolutionConfiguration.Name</c>, which is the bare name (<c>Release</c>), so a
    /// platform-qualified request (<c>Release|Any CPU</c>) matches nothing and the existing active
    /// configuration is kept. The caller reports the returned name so that substitution is visible
    /// to the agent instead of being reported as a successful build of what it asked for.
    /// Pass <see langword="null"/> to read the active configuration without changing it.
    /// </summary>
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
            // This read only reports what happened; a solution still loading can refuse it, and
            // that must not turn the build the caller actually asked for into an error reply.
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

    /// <summary>
    /// The error a failed synchronous DTE call is reported as. Raw COM/HRESULT text ("Exception from
    /// HRESULT: 0x80010001") is meaningless to the agent on the other end of the pipe and can leak
    /// host implementation detail, so it is replaced with a flat, actionable message; the one
    /// HRESULT that is not a real failure - Visual Studio rejecting the call because it is busy with
    /// another automation call or a modal dialog - is reported as a retry instead.
    /// </summary>
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

    /// <summary>
    /// Adds an existing project file to the open solution. <c>Solution.AddFromFile</c> is a
    /// synchronous, uncancellable COM call that loads the project and can trigger a NuGet restore,
    /// so - unlike every method whose wait this server owns - it cannot be bounded here: there is
    /// no completion signal to race a <see cref="CancellationToken"/> against, and abandoning the
    /// wait would only leave Visual Studio still loading. The budget therefore has to live on the
    /// client, which puts this method in the same long bucket as the build methods
    /// (<c>VsControlPipeClient._buildTimeout</c>); a 60 s budget here would time out mid-load and
    /// invite a retry that adds the project a second time.
    /// </summary>
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

    /// <summary>
    /// Closes the current solution (saving first) and opens another one from inside the workspace.
    /// <c>Solution.Close</c> and <c>Solution.Open</c> are synchronous, uncancellable COM calls, so
    /// this method carries no server-side bound for the same reason
    /// <see cref="AddProjectToSolutionAsync"/> does not, and is on the client's long budget
    /// (<c>VsControlPipeClient._buildTimeout</c>). That budget is load-bearing rather than
    /// cosmetic: under the 60 s request budget the agent is told the call timed out while Visual
    /// Studio is still loading, and the natural retry closes and reopens the user's solution a
    /// second time.
    /// </summary>
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

    /// <summary>Counts Error List errors/warnings, optionally narrowed to one project so a
    /// single-project build does not report unrelated projects' diagnostics as its own.</summary>
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
