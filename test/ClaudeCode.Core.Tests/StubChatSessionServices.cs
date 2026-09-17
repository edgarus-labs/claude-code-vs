using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

internal sealed class StubChatSessionServices : IChatSessionServices
{
    public StubChatSessionServices(IAcpAgentConnectionFactory connectionFactory, IAcpAuthService authService, string? workspaceRoot = null)
    {
        ConnectionFactory = connectionFactory;
        AuthService = authService;
        WorkspaceRoot = workspaceRoot;
    }

    public IAcpAgentConnectionFactory ConnectionFactory { get; }

    public IAcpAuthService AuthService { get; }

    public string? WorkspaceRoot { get; }

    public Func<CancellationToken, Task<EditorDocumentSnapshot?>>? CaptureHandler { get; set; }

    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        CaptureHandler?.Invoke(cancellationToken) ?? Task.FromResult<EditorDocumentSnapshot?>(null);
}
