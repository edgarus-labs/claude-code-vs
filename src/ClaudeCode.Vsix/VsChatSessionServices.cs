using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix;

internal sealed class VsChatSessionServices : IChatSessionServices
{
    private readonly WorkspaceRootTracker _workspaceRootTracker;
    private readonly ActiveEditorDocumentTracker _editorDocumentTracker;
    private readonly Func<bool> _remoteControlAtStartup;

    public VsChatSessionServices(IAcpAgentConnectionFactory connectionFactory, IAcpAuthService authService, IUsageService usageService, WorkspaceRootTracker workspaceRootTracker, ActiveEditorDocumentTracker editorDocumentTracker, Func<bool> remoteControlAtStartup)
    {
        ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        AuthService = authService ?? throw new ArgumentNullException(nameof(authService));
        UsageService = usageService ?? throw new ArgumentNullException(nameof(usageService));
        _workspaceRootTracker = workspaceRootTracker ?? throw new ArgumentNullException(nameof(workspaceRootTracker));
        _editorDocumentTracker = editorDocumentTracker ?? throw new ArgumentNullException(nameof(editorDocumentTracker));
        _remoteControlAtStartup = remoteControlAtStartup ?? throw new ArgumentNullException(nameof(remoteControlAtStartup));
    }

    public IAcpAgentConnectionFactory ConnectionFactory { get; }

    public IAcpAuthService AuthService { get; }

    public IUsageService UsageService { get; }

    public string? WorkspaceRoot => _workspaceRootTracker.Root;

    public bool HasActiveDocument => _editorDocumentTracker.HasActiveDocument;

    public bool RemoteControlAtStartup => _remoteControlAtStartup();

    public event EventHandler? ActiveDocumentChanged
    {
        add => _editorDocumentTracker.ActiveDocumentChanged += value;
        remove => _editorDocumentTracker.ActiveDocumentChanged -= value;
    }

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
            throw new IOException("The editor rejected the document edit.");
        edit.Apply();

        if (edit.HasFailedChanges || edit.Canceled)
            throw new IOException("The editor rejected the document edit.");
        return true;
    }
}
