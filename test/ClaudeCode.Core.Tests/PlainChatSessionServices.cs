using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

internal class PlainChatSessionServices : IChatSessionServices
{
    private readonly StubChatSessionServices _inner;

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

    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        _inner.CaptureActiveDocumentAsync(cancellationToken);

    public Task OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken) =>
        _inner.OpenDocumentAsync(path, line, cancellationToken);

    public Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken) =>
        _inner.TryReadOpenDocumentAsync(path, cancellationToken);

    public Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken) =>
        _inner.TryWriteOpenDocumentAsync(path, text, cancellationToken);

    public Task<bool> ConfirmSignOutEverywhereAsync(CancellationToken cancellationToken) =>
        _inner.ConfirmSignOutEverywhereAsync(cancellationToken);
}
