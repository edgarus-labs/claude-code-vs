using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix;

internal sealed class VsChatSessionServices : IChatSessionServices, IAutoEffortServices
{
    private readonly WorkspaceRootTracker _workspaceRootTracker;
    private readonly ActiveEditorDocumentTracker _editorDocumentTracker;
    private readonly Func<bool> _remoteControlAtStartup;

    public VsChatSessionServices(IAcpAgentConnectionFactory connectionFactory, IAcpAuthService authService, IUsageService usageService, WorkspaceRootTracker workspaceRootTracker, ActiveEditorDocumentTracker editorDocumentTracker, Func<bool> remoteControlAtStartup, IEffortClassifier effortClassifier, IAcpAdapterInstaller? adapterInstaller = null)
    {
        ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        AuthService = authService ?? throw new ArgumentNullException(nameof(authService));
        UsageService = usageService ?? throw new ArgumentNullException(nameof(usageService));
        _workspaceRootTracker = workspaceRootTracker ?? throw new ArgumentNullException(nameof(workspaceRootTracker));
        _editorDocumentTracker = editorDocumentTracker ?? throw new ArgumentNullException(nameof(editorDocumentTracker));
        _remoteControlAtStartup = remoteControlAtStartup ?? throw new ArgumentNullException(nameof(remoteControlAtStartup));
        EffortClassifier = effortClassifier ?? throw new ArgumentNullException(nameof(effortClassifier));
        AdapterInstaller = adapterInstaller;
    }

    public IAcpAgentConnectionFactory ConnectionFactory { get; }

    public IAcpAuthService AuthService { get; }

    public IUsageService UsageService { get; }

    public IAcpAdapterInstaller? AdapterInstaller { get; }

    public string? WorkspaceRoot => _workspaceRootTracker.Root;

    public bool HasActiveDocument => _editorDocumentTracker.HasActiveDocument;

    public bool RemoteControlAtStartup => _remoteControlAtStartup();

    public IEffortClassifier? EffortClassifier { get; }

    // Errors go to the "Claude Code" Output pane (Output > Show output from) as well as the ActivityLog,
    // which is only written when Visual Studio runs with /log. Only errors are logged here.
    private static OutputWindowPane? _outputPane;

    public void LogError(string message, Exception exception)
    {
        var line = "[Error] " + message + " " + exception.GetType().Name + ": " + exception.Message;
        ActivityLog.TryLogError("Claude Code", message + " " + exception);
        // Failures are logged inside, and FileAndForget reports the fault to VS telemetry.
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                _outputPane ??= await VS.Windows.CreateOutputWindowPaneAsync("Claude Code");
                await _outputPane.WriteLineAsync(line);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ActivityLog.TryLogError("Claude Code", "Could not write to the Output pane: " + ex);
            }
        }).FileAndForget("claudecode/output-log");
    }

#pragma warning restore VSSDK007

    /// <summary>
    /// Writes the specified line to the &quot;Claude Code&quot; Visual Studio Output pane, lazily creating the pane on first use and fire-and-forget logging any failure to the Activity Log.
    /// </summary>
    /// <param name="line">The line.</param>
    public static void LogOutputLine(string line) =>
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                _outputPane ??= await VS.Windows.CreateOutputWindowPaneAsync("Claude Code");
                await _outputPane.WriteLineAsync(line);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ActivityLog.TryLogError("Claude Code", "Could not write to the Output pane: " + ex);
            }
        }).FileAndForget("claudecode/output-log");
#pragma warning restore VSSDK007

    /// <summary>
    /// Occurs when active document changed.
    /// </summary>
    public event EventHandler? ActiveDocumentChanged
    {
        add => _editorDocumentTracker.ActiveDocumentChanged += value;
        remove => _editorDocumentTracker.ActiveDocumentChanged -= value;
    }

    /// <summary>
    /// Occurs when workspace root changed.
    /// </summary>
    public event EventHandler? WorkspaceRootChanged
    {
        add => _workspaceRootTracker.Changed += value;
        remove => _workspaceRootTracker.Changed -= value;
    }

    /// <summary>
    /// Asynchronously captures a snapshot of the currently active editor document from the document tracker, returning null if no document is active.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the editor document snapshot?.</returns>
    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        _editorDocumentTracker.CaptureActiveDocumentAsync(cancellationToken);

    /// <summary>
    /// Asynchronously opens the specified document on the main thread, optionally positions the editor caret at the given line, and throws an InvalidOperationException if the shell fails to open the document.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when an error occurs during execution.</exception>
    public async Task OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        // A null view means the shell did not open the document (deleted, locked, or not something
        // it can display). Returning normally there would report success for a click that did
        // nothing; the contract on IChatSessionServices.OpenDocumentAsync is to fault so the
        // caller can tell the user, which is what VsControlPipeServer.OpenDocumentAsync does too.
        var view = await VS.Documents.OpenAsync(path) ??
            throw new InvalidOperationException($"'{path}' could not be opened.");
        if (line.HasValue && view.TextView is not null && view.TextBuffer is not null)
        {
            EditorCaret.MoveToLine(view.TextView, view.TextBuffer, line.Value);
        }
    }

    /// <summary>
    /// Asynchronously switches to the main thread and returns the current text of the open document at the specified path, or null if the document is not open in a text view.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the string?.</returns>
    public async Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var view = await VS.Documents.GetDocumentViewAsync(path);
        if (view?.TextBuffer is null)
        {
            return null;
        }

        return view.TextBuffer.CurrentSnapshot.GetText();
    }

    /// <summary>
    /// Asynchronously replaces the entire text of an open document in the Visual Studio editor on the main thread, returning false if the document is not open and throwing an IOException if the editor rejects the edit.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    /// <exception cref="IOException">Thrown when an error occurs during execution.</exception>
    public async Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var view = await VS.Documents.GetDocumentViewAsync(path);
        if (view?.TextBuffer is null)
        {
            return false;
        }

        using var edit = view.TextBuffer.CreateEdit();
        if (!edit.Replace(new Span(0, view.TextBuffer.CurrentSnapshot.Length), text) || edit.HasFailedChanges)
        {
            throw new IOException("The editor rejected the document edit.");
        }

        edit.Apply();

        if (edit.HasFailedChanges || edit.Canceled)
        {
            throw new IOException("The editor rejected the document edit.");
        }

        return true;
    }

    /// <summary>
    /// Asynchronously prompts the user on the main UI thread with a Yes/No confirmation dialog warning that signing out applies to Claude Code across all clients on the machine, and returns true only if the user confirms.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public async Task<bool> ConfirmSignOutEverywhereAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        int result = VsShellUtilities.ShowMessageBox(
            ServiceProvider.GlobalProvider,
            "This signs you out of Claude Code everywhere on this machine (the CLI, VS Code and other clients), not only Visual Studio. Continue?",
            "Claude Code",
            OLEMSGICON.OLEMSGICON_QUERY,
            OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);

        return result == (int)VSConstants.MessageBoxResult.IDYES;
    }

    /// <summary>
    /// Copies the specified text to the system clipboard on the main UI thread, logging any failure and reporting it through a fire-and-forget task.
    /// </summary>
    /// <param name="text">The text.</param>
    public void CopyToClipboard(string text) =>
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
            }
            catch (Exception ex)
            {
                LogError("Could not copy text to clipboard.", ex);
            }
        }).FileAndForget("claudecode/copy-clipboard");
#pragma warning restore VSSDK007

}
