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
    public string? WorkspaceRoot => null;

    /// <summary>
    /// Gets a value indicating whether has active document.
    /// </summary>
    public bool HasActiveDocument => false;

    /// <summary>
    /// Gets a value indicating whether remote control at startup.
    /// </summary>
    public bool RemoteControlAtStartup => false;

    /// <summary>
    /// Occurs when active document changed.
    /// </summary>
    public event EventHandler? ActiveDocumentChanged { add { } remove { } }

    /// <summary>
    /// Occurs when workspace root changed.
    /// </summary>
    public event EventHandler? WorkspaceRootChanged { add { } remove { } }

    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<EditorDocumentSnapshot?>(null);

    /// <summary>Always faults with <see cref="InvalidOperationException"/>.</summary>
    public Task OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("No host editor is available to open documents."));

    public Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <summary>Always returns false.</summary>
    public Task<bool> ConfirmSignOutEverywhereAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}
