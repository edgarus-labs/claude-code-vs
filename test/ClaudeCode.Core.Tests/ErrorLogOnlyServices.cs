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

    public IAcpAgentConnectionFactory ConnectionFactory => _inner.ConnectionFactory;

    public IAcpAuthService AuthService => _inner.AuthService;

    public IUsageService UsageService => _inner.UsageService;

    public string? WorkspaceRoot => _inner.WorkspaceRoot;

    public bool HasActiveDocument => _inner.HasActiveDocument;

    public event EventHandler? ActiveDocumentChanged
    {
        add => _inner.ActiveDocumentChanged += value;
        remove => _inner.ActiveDocumentChanged -= value;
    }

    public event EventHandler? WorkspaceRootChanged
    {
        add => _inner.WorkspaceRootChanged += value;
        remove => _inner.WorkspaceRootChanged -= value;
    }

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

internal sealed class ErrorLogOnlyServices : PlainChatSessionServices, IChatErrorLog
{
    public ErrorLogOnlyServices(StubChatSessionServices inner) : base(inner) { }

    public List<(string Message, Exception Exception)> LoggedErrors { get; } = [];

    public bool ThrowOnLog { get; set; }

    public void LogError(string message, Exception exception)
    {
        LoggedErrors.Add((message, exception));
        if (ThrowOnLog) throw new InvalidOperationException("log failed");
    }
}
