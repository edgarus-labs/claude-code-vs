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

    public event EventHandler? WorkspaceRootChanged { add { } remove { } }

    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<EditorDocumentSnapshot?>(null);

    /// <summary>This double has no host editor at all, so an open can only fail. The interface
    /// contract says the returned task carries that failure; reporting success would tell the caller a
    /// file was shown to the user that never was.</summary>
    public Task OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("No host editor is available to open documents."));

    public Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
