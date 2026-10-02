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

    public VsChatSessionServices(IAcpAgentConnectionFactory connectionFactory, IAcpAuthService authService, IUsageService usageService, WorkspaceRootTracker workspaceRootTracker, ActiveEditorDocumentTracker editorDocumentTracker, Func<bool> remoteControlAtStartup, IEffortClassifier effortClassifier)
    {
        ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        AuthService = authService ?? throw new ArgumentNullException(nameof(authService));
        UsageService = usageService ?? throw new ArgumentNullException(nameof(usageService));
        _workspaceRootTracker = workspaceRootTracker ?? throw new ArgumentNullException(nameof(workspaceRootTracker));
        _editorDocumentTracker = editorDocumentTracker ?? throw new ArgumentNullException(nameof(editorDocumentTracker));
        _remoteControlAtStartup = remoteControlAtStartup ?? throw new ArgumentNullException(nameof(remoteControlAtStartup));
        EffortClassifier = effortClassifier ?? throw new ArgumentNullException(nameof(effortClassifier));
    }

    /// <summary>
    /// Gets the connection factory.
    /// </summary>
    public IAcpAgentConnectionFactory ConnectionFactory { get; }

    /// <summary>
    /// Gets the auth service.
    /// </summary>
    public IAcpAuthService AuthService { get; }

    /// <summary>
    /// Gets the usage service.
    /// </summary>
    public IUsageService UsageService { get; }

    /// <summary>
    /// Gets the workspace root.
    /// </summary>
    public string? WorkspaceRoot => _workspaceRootTracker.Root;

    /// <summary>
    /// Gets a value indicating whether has active document.
    /// </summary>
    public bool HasActiveDocument => _editorDocumentTracker.HasActiveDocument;

    /// <summary>
    /// Gets a value indicating whether remote control at startup.
    /// </summary>
    public bool RemoteControlAtStartup => _remoteControlAtStartup();

    /// <summary>
    /// Gets the effort classifier.
    /// </summary>
    public IEffortClassifier? EffortClassifier { get; }

    private static OutputWindowPane? _outputPane;

    public void LogError(string message, Exception exception)
    {
        var line = "[Error] " + message + " " + exception.GetType().Name + ": " + exception.Message;
        ActivityLog.TryLogError("Claude Code", message + " " + exception);
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

    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        _editorDocumentTracker.CaptureActiveDocumentAsync(cancellationToken);

    public async Task OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var view = await VS.Documents.OpenAsync(path) ??
            throw new InvalidOperationException($"'{path}' could not be opened.");
        if (line.HasValue && view.TextView is not null && view.TextBuffer is not null)
        {
            EditorCaret.MoveToLine(view.TextView, view.TextBuffer, line.Value);
        }
    }

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
}
