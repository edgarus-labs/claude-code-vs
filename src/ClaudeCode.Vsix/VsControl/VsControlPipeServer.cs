using ClaudeCode.Contracts;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.VsControl;

internal sealed class VsControlPipeServer : IAsyncDisposable
{
    private static readonly JsonSerializerSettings _envelopeSettings = new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    };

    // RPC_E_SERVERCALL_RETRYLATER: the VS main-thread COM message pump momentarily refused this call
    // (e.g. it is mid another automation call or a modal dialog is up); not a real failure.
    private const int _rpcServerCallRetryLaterHResult = unchecked((int)0x8001010A);

    // Only commands that never execute code from the open solution/workspace are allow-listed here.
    // Debug.Start, Debug.StartWithoutDebugging, Build.BuildSolution, and Build.RebuildSolution are
    // deliberately excluded: an ACP agent (or anything impersonating one over this pipe) must not be
    // able to trigger arbitrary code execution by driving the debugger or MSBuild.
    private static readonly HashSet<string> _allowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Edit.FormatDocument",
        "Edit.FormatSelection",
        "Debug.StopDebugging",
        "File.SaveAll",
        "View.ErrorList",
    };

    private readonly string _pipeName;
    private readonly string? _workspaceRoot;
    private readonly string _token;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Microsoft.VisualStudio.Threading.JoinableTask? _listenTask;
    private volatile NamedPipeServerStream? _pipe;

    public VsControlPipeServer(string pipeName, string? workspaceRoot, string token)
    {
        _pipeName = pipeName;
        _workspaceRoot = workspaceRoot;
        _token = token ?? string.Empty;
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
        while (!cancellationToken.IsCancellationRequested)
        {
            try
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

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using var reader = new StreamReader(pipe, utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, utf8NoBom, bufferSize: 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

                if (!await TryHandshakeAsync(reader).ConfigureAwait(false))
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
        }
    }

    private async Task<bool> TryHandshakeAsync(StreamReader reader)
    {
        string? tokenLine;
        try
        {
            tokenLine = await reader.ReadLineAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }

        return tokenLine is not null && ConstantTimeTokenComparer.Equals(tokenLine, _token);
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var owner = WindowsIdentity.GetCurrent().Owner
            ?? throw new InvalidOperationException("Unable to determine the current Windows user identity.");

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return security;
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
            case "buildSolution": return (await BuildSolutionAsync(args)).ToString(Formatting.None);
            case "getBuildErrors": return (await GetBuildErrorsAsync()).ToString(Formatting.None);
            case "getDiagnostics": return (await GetDiagnosticsAsync(args)).ToString(Formatting.None);
            case "runCommand": return (await RunCommandAsync(args)).ToString(Formatting.None);
            case "getSolutionInfo": return (await GetSolutionInfoAsync()).ToString(Formatting.None);
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
        if (!WorkspacePathGuard.TryResolveWithinWorkspace(_workspaceRoot, path, out var fullPath))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the workspace.");
        }

        var view = await VS.Documents.OpenAsync(fullPath);
        if (view is null)
        {
            throw new InvalidOperationException($"'{path}' could not be opened.");
        }

        var line = args["line"]?.Value<int?>();
        if (line.HasValue && view.TextView is not null && view.TextBuffer is not null)
        {
            MoveCaretToLine(view.TextView, view.TextBuffer, line.Value);
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
        if (!WorkspacePathGuard.TryResolveWithinWorkspace(_workspaceRoot, path, out var fullPath))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the workspace.");
        }

        var view = await VS.Documents.GetDocumentViewAsync(fullPath) ?? await VS.Documents.OpenAsync(fullPath);
        if (view?.TextView is null || view.TextBuffer is null)
        {
            throw new InvalidOperationException($"'{path}' could not be opened.");
        }

        var span = view.TextView.Selection.StreamSelectionSpan.SnapshotSpan;
        var edit = view.TextBuffer.CreateEdit();
        bool canceled;
        try
        {
            edit.Replace(span.Span, text);
            edit.Apply();
            canceled = edit.Canceled;
        }
        finally
        {
            edit.Dispose();
        }

        if (canceled)
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
    /// Builds the current solution and waits for completion. <c>errorCount</c>/<c>warningCount</c>
    /// reflect the Error List window's contents right after the build - which are themselves subject
    /// to the Error List's own Build/IntelliSense scope filters - not a raw MSBuild diagnostic count.
    /// The VS SDK does not expose MSBuild's own diagnostic totals without driving
    /// <c>IVsSolutionBuildManager</c> directly; the Error List is the diagnostic surface
    /// <see cref="Community.VisualStudio.Toolkit"/> already gives us.
    /// </summary>
    private static async Task<JObject> BuildSolutionAsync(JObject args)
    {
        // The optional `configuration` param only takes effect if it matches an existing solution
        // configuration name; VsControlProtocol.md leaves per-configuration switching unspecified, so a
        // mismatched or omitted value simply builds whatever configuration is currently active.
        var configurationName = args["configuration"]?.Value<string>();
        if (!string.IsNullOrEmpty(configurationName))
        {
            await TrySetActiveConfigurationAsync(configurationName!);
        }

        var succeeded = await VS.Build.BuildSolutionAsync();
        var (errorCount, warningCount) = await CountBuildDiagnosticsAsync();

        return new JObject
        {
            ["succeeded"] = succeeded,
            ["errorCount"] = errorCount,
            ["warningCount"] = warningCount,
        };
    }

    private static async Task TrySetActiveConfigurationAsync(string configurationName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await VS.GetRequiredServiceAsync<DTE, DTE>();
        var solutionBuild = dte.Solution?.SolutionBuild;
        if (solutionBuild is null)
        {
            return;
        }

        foreach (SolutionConfiguration configuration in solutionBuild.SolutionConfigurations)
        {
            if (string.Equals(configuration.Name, configurationName, StringComparison.OrdinalIgnoreCase))
            {
                configuration.Activate();

                return;
            }
        }
    }

    private static async Task<JObject> GetBuildErrorsAsync()
    {
        var items = await GetErrorListItemsAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var errors = new JArray();
        foreach (var item in items)
        {
            errors.Add(new JObject
            {
                ["file"] = item.FileName,
                ["line"] = item.Line,
                ["column"] = item.Column,
                ["message"] = item.Description,
                ["severity"] = ErrorLevelToSeverity(item.ErrorLevel),
            });
        }

        return new JObject { ["errors"] = errors };
    }

    private async Task<JObject> GetDiagnosticsAsync(JObject args)
    {
        var path = RequireString(args, "path");
        if (!WorkspacePathGuard.TryResolveWithinWorkspace(_workspaceRoot, path, out var fullPath))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the workspace.");
        }

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
        catch (COMException ex) when (ex.HResult == _rpcServerCallRetryLaterHResult)
        {
            throw new InvalidOperationException("Visual Studio is busy, try again.");
        }
        catch (COMException)
        {
            // Swallow the raw COM/HRESULT text (e.g. "Exception from HRESULT: 0x80010001") - it is
            // meaningless to the agent on the other end of the pipe and can leak host implementation
            // detail; a flat, actionable message is all a tool caller needs.
            throw new InvalidOperationException($"Command '{commandName}' could not be executed.");
        }

        return new JObject();
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

    private static async Task<(int ErrorCount, int WarningCount)> CountBuildDiagnosticsAsync()
    {
        var items = await GetErrorListItemsAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var errorCount = 0;
        var warningCount = 0;
        foreach (var item in items)
        {
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

    private static void MoveCaretToLine(IWpfTextView textView, Microsoft.VisualStudio.Text.ITextBuffer textBuffer, int line)
    {
        var snapshot = textBuffer.CurrentSnapshot;
        var lineNumber = Math.Max(0, Math.Min(line - 1, snapshot.LineCount - 1));
        var textLine = snapshot.GetLineFromLineNumber(lineNumber);
        textView.Caret.MoveTo(textLine.Start);
        textView.ViewScroller.EnsureSpanVisible(new Microsoft.VisualStudio.Text.SnapshotSpan(textLine.Start, 0));
    }

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
