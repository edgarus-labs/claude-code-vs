using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
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
}
