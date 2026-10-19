using ClaudeCode.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels.Demo;

public sealed class NullChatSessionServices : IChatSessionServices
{
    /// <summary>
    /// Initializes a new instance of the NullChatSessionServices class with fake connection factory and authentication services and a null usage service for use in testing or non-production scenarios.
    /// </summary>
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
    /// Gets the adapter installer.
    /// </summary>
    public IAcpAdapterInstaller? AdapterInstaller => null;

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

    /// <summary>
    /// Asynchronously captures a snapshot of the currently active editor document, returning null when no document is available or the capture is not supported.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the editor document snapshot?.</returns>
    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<EditorDocumentSnapshot?>(null);

    /// <summary>This double has no host editor at all, so an open can only fail. The interface
    /// contract says the returned task carries that failure; reporting success would tell the caller a
    /// file was shown to the user that never was.</summary>
    public Task OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("No host editor is available to open documents."));

    /// <summary>
    /// Asynchronously attempts to read the contents of a currently open document at the specified path, returning null when the document is not open or cannot be read.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the string?.</returns>
    public Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Asynchronously attempts to write the specified text to an open document at the given path, returning false when the document is not open or the write is not supported.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <summary>There is no user to ask, so a machine-wide sign-out is never confirmed on their behalf.</summary>
    public Task<bool> ConfirmSignOutEverywhereAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>
    /// Copies the specified text to the system clipboard so it can be pasted into other applications.
    /// </summary>
    /// <param name="text">The text.</param>
    public void CopyToClipboard(string text) { }
}
