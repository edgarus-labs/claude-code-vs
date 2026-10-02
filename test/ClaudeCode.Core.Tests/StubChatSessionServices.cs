using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

internal sealed class StubChatSessionServices : IChatSessionServices, IAutoEffortServices
{
    public StubChatSessionServices(IAcpAgentConnectionFactory connectionFactory, IAcpAuthService authService, string? workspaceRoot = null, IUsageService? usageService = null)
    {
        ConnectionFactory = connectionFactory;
        AuthService = authService;
        _workspaceRoot = workspaceRoot;
        UsageService = usageService ?? new ClaudeCode.Core.ViewModels.Demo.NullUsageService();
    }

    private string? _workspaceRoot;

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
    /// Gets or sets the effort classifier.
    /// </summary>
    public IEffortClassifier? EffortClassifier { get; set; }

    /// <summary>
    /// Gets or sets the workspace root handler.
    /// </summary>
    public Func<string?>? WorkspaceRootHandler { get; set; }

    /// <summary>
    /// Gets the workspace root.
    /// </summary>
    public string? WorkspaceRoot => WorkspaceRootHandler is null ? _workspaceRoot : WorkspaceRootHandler();

    /// <summary>
    /// Gets or sets a value indicating whether has active document.
    /// </summary>
    public bool HasActiveDocument { get; private set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether remote control at startup.
    /// </summary>
    public bool RemoteControlAtStartup { get; set; }

    /// <summary>
    /// Gets the collection of logged errors.
    /// </summary>
    public List<(string Message, Exception Exception)> LoggedErrors { get; } = [];

    public void LogError(string message, Exception exception) => LoggedErrors.Add((message, exception));

    /// <summary>
    /// Occurs when active document changed.
    /// </summary>
    public event EventHandler? ActiveDocumentChanged;

    /// <summary>
    /// Occurs when workspace root changed.
    /// </summary>
    public event EventHandler? WorkspaceRootChanged;

    public void SetHasActiveDocument(bool value)
    {
        HasActiveDocument = value;
        ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetWorkspaceRoot(string? value)
    {
        _workspaceRoot = value;
        WorkspaceRootChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets or sets the capture handler.
    /// </summary>
    public Func<CancellationToken, Task<EditorDocumentSnapshot?>>? CaptureHandler { get; set; }

    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        CaptureHandler?.Invoke(cancellationToken) ?? Task.FromResult<EditorDocumentSnapshot?>(null);

    /// <summary>
    /// Gets the open documents.
    /// </summary>
    public Dictionary<string, string> OpenDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the collection of opened document paths.
    /// </summary>
    public List<string> OpenedDocumentPaths { get; } = new();

    /// <summary>
    /// Gets the collection of opened document lines.
    /// </summary>
    public List<int?> OpenedDocumentLines { get; } = new();

    /// <summary>
    /// Gets or sets the open document handler.
    /// </summary>
    public Func<string, int?, CancellationToken, Task>? OpenDocumentHandler { get; set; }

    public Task OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken)
    {
        OpenedDocumentPaths.Add(path);
        OpenedDocumentLines.Add(line);
        return OpenDocumentHandler?.Invoke(path, line, cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// Gets or sets the read open document handler.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? ReadOpenDocumentHandler { get; set; }

    /// <summary>
    /// Gets or sets the write open document handler.
    /// </summary>
    public Func<string, string, CancellationToken, Task<bool>>? WriteOpenDocumentHandler { get; set; }

    public Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (ReadOpenDocumentHandler is not null)
        {
            return ReadOpenDocumentHandler(path, cancellationToken);
        }

        return Task.FromResult(OpenDocuments.TryGetValue(path, out var text) ? text : null);
    }

    public Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken)
    {
        if (WriteOpenDocumentHandler is not null)
        {
            return WriteOpenDocumentHandler(path, text, cancellationToken);
        }

        if (!OpenDocuments.ContainsKey(path))
        {
            return Task.FromResult(false);
        }

        OpenDocuments[path] = text;
        return Task.FromResult(true);
    }

    /// <summary>
    /// Gets or sets a value indicating whether confirm sign out response.
    /// </summary>
    public bool ConfirmSignOutResponse { get; set; } = true;

    /// <summary>
    /// Gets the collection of confirm sign out requests.
    /// </summary>
    public List<CancellationToken> ConfirmSignOutRequests { get; } = new();

    /// <summary>
    /// Gets or sets the confirm sign out handler.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? ConfirmSignOutHandler { get; set; }

    public Task<bool> ConfirmSignOutEverywhereAsync(CancellationToken cancellationToken)
    {
        ConfirmSignOutRequests.Add(cancellationToken);
        return ConfirmSignOutHandler?.Invoke(cancellationToken) ?? Task.FromResult(ConfirmSignOutResponse);
    }
}
