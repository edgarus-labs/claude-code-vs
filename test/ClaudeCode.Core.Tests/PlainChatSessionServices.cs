using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

/// <summary>A host with none of the optional capabilities: <see cref="StubChatSessionServices"/> seen
/// through <see cref="IChatSessionServices"/> alone. A capability the stub gains later is not
/// forwarded unless a subclass declares it.</summary>
internal class PlainChatSessionServices : IChatSessionServices
{
    private readonly StubChatSessionServices _inner;

    /// <summary>
    /// Initializes a new instance of the PlainChatSessionServices class with the specified stub chat session services as the inner delegate.
    /// </summary>
    /// <param name="inner">The inner.</param>
    public PlainChatSessionServices(StubChatSessionServices inner) => _inner = inner;

    /// <summary>
    /// Gets the connection factory.
    /// </summary>
    public IAcpAgentConnectionFactory ConnectionFactory => _inner.ConnectionFactory;

    /// <summary>
    /// Gets the auth service.
    /// </summary>
    public IAcpAuthService AuthService => _inner.AuthService;

    /// <summary>
    /// Gets the usage service.
    /// </summary>
    public IUsageService UsageService => _inner.UsageService;

    /// <summary>
    /// Gets the adapter installer.
    /// </summary>
    public IAcpAdapterInstaller? AdapterInstaller => _inner.AdapterInstaller;


    /// <summary>
    /// Gets the workspace root.
    /// </summary>
    public string? WorkspaceRoot => _inner.WorkspaceRoot;

    /// <summary>
    /// Gets a value indicating whether has active document.
    /// </summary>
    public bool HasActiveDocument => _inner.HasActiveDocument;

    /// <summary>
    /// Occurs when active document changed.
    /// </summary>
    public event EventHandler? ActiveDocumentChanged
    {
        add => _inner.ActiveDocumentChanged += value;
        remove => _inner.ActiveDocumentChanged -= value;
    }

    /// <summary>
    /// Occurs when workspace root changed.
    /// </summary>
    public event EventHandler? WorkspaceRootChanged
    {
        add => _inner.WorkspaceRootChanged += value;
        remove => _inner.WorkspaceRootChanged -= value;
    }

    /// <summary>
    /// Gets a value indicating whether remote control at startup.
    /// </summary>
    public bool RemoteControlAtStartup => _inner.RemoteControlAtStartup;

    /// <summary>
    /// Asynchronously captures an immutable snapshot of the currently active editor document by delegating to the underlying editor service, returning null when no document is active.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the editor document snapshot?.</returns>
    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        _inner.CaptureActiveDocumentAsync(cancellationToken);

    /// <summary>
    /// Asynchronously opens the document at the specified path, optionally navigating to the given line, by delegating to the underlying inner service.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken) =>
        _inner.OpenDocumentAsync(path, line, cancellationToken);

    /// <summary>
    /// Asynchronously attempts to read the contents of a document at the specified path that is currently open in the host application, returning null if no such open document is available.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the string?.</returns>
    public Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken) =>
        _inner.TryReadOpenDocumentAsync(path, cancellationToken);

    /// <summary>
    /// Asynchronously attempts to write the specified text to an open document at the given path by delegating to the underlying implementation, returning true if the write succeeds and false otherwise.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken) =>
        _inner.TryWriteOpenDocumentAsync(path, text, cancellationToken);

    /// <summary>
    /// Asynchronously prompts the user to confirm a sign-out from all sessions and returns a value indicating whether the confirmation was granted.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public Task<bool> ConfirmSignOutEverywhereAsync(CancellationToken cancellationToken) =>
        _inner.ConfirmSignOutEverywhereAsync(cancellationToken);

    /// <summary>
    /// Copies the specified text to the system clipboard by delegating the operation to the underlying clipboard service.
    /// </summary>
    /// <param name="text">The text.</param>
    public void CopyToClipboard(string text) => _inner.CopyToClipboard(text);
}
