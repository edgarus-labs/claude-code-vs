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

    public Dictionary<string, string> OpenDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Func<string, CancellationToken, Task<string?>>? ReadOpenDocumentHandler { get; set; }

    public Func<string, string, CancellationToken, Task<bool>>? WriteOpenDocumentHandler { get; set; }

    public Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (ReadOpenDocumentHandler is not null) return ReadOpenDocumentHandler(path, cancellationToken);
        return Task.FromResult(OpenDocuments.TryGetValue(path, out var text) ? text : null);
    }

    public Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken)
    {
        if (WriteOpenDocumentHandler is not null) return WriteOpenDocumentHandler(path, text, cancellationToken);
        if (!OpenDocuments.ContainsKey(path)) return Task.FromResult(false);
        OpenDocuments[path] = text;
        return Task.FromResult(true);
    }
}
