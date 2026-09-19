using ClaudeCode.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels.Demo;

public sealed class NullChatSessionServices : IChatSessionServices
{
    public NullChatSessionServices()
    {
        ConnectionFactory = new FakeAcpAgentConnectionFactory();
        AuthService = new FakeAcpAuthService();
        UsageService = new NullUsageService();
    }

    public IAcpAgentConnectionFactory ConnectionFactory { get; }

    public IAcpAuthService AuthService { get; }

    public IUsageService UsageService { get; }

    public string? WorkspaceRoot => null;

    public bool HasActiveDocument => false;

    public bool RemoteControlAtStartup => false;

    public event EventHandler? ActiveDocumentChanged { add { } remove { } }

    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<EditorDocumentSnapshot?>(null);

    public Task OpenDocumentAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
