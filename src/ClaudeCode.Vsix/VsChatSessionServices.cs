using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix;

internal sealed class VsChatSessionServices : IChatSessionServices
{
    private readonly Func<string?> _getWorkspaceRoot;
    private readonly ActiveEditorDocumentTracker _editorDocumentTracker;

    public VsChatSessionServices(IAcpAgentConnectionFactory connectionFactory, IAcpAuthService authService, Func<string?> getWorkspaceRoot, ActiveEditorDocumentTracker editorDocumentTracker)
    {
        ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        AuthService = authService ?? throw new ArgumentNullException(nameof(authService));
        _getWorkspaceRoot = getWorkspaceRoot ?? throw new ArgumentNullException(nameof(getWorkspaceRoot));
        _editorDocumentTracker = editorDocumentTracker ?? throw new ArgumentNullException(nameof(editorDocumentTracker));
    }

    public IAcpAgentConnectionFactory ConnectionFactory { get; }

    public IAcpAuthService AuthService { get; }

    public string? WorkspaceRoot => _getWorkspaceRoot();

    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        _editorDocumentTracker.CaptureActiveDocumentAsync(cancellationToken);

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
        edit.Replace(new Span(0, view.TextBuffer.CurrentSnapshot.Length), text);
        edit.Apply();

        // Apply() can silently fail to commit (e.g. a read-only region) without throwing;
        // HasFailedChanges/Canceled are the documented signals that the buffer was not changed.
        return !edit.HasFailedChanges && !edit.Canceled;
    }
}
