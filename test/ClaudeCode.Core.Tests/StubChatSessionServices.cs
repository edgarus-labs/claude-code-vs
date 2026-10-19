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

    public IAcpAgentConnectionFactory ConnectionFactory { get; }

    public IAcpAuthService AuthService { get; }

    public IUsageService UsageService { get; }

    public IAcpAdapterInstaller? AdapterInstaller { get; set; }

    public IEffortClassifier? EffortClassifier { get; set; }

    /// <summary>Injects a failure for the VSIX host's <c>WorkspaceRoot</c>, which is a live
    /// callback into solution state and throws while a solution is closing or reloading.</summary>
    public Func<string?>? WorkspaceRootHandler { get; set; }

    public string? WorkspaceRoot => WorkspaceRootHandler is null ? _workspaceRoot : WorkspaceRootHandler();

    public bool HasActiveDocument { get; private set; } = true;

    public bool RemoteControlAtStartup { get; set; }

    public List<(string Message, Exception Exception)> LoggedErrors { get; } = [];

    /// <summary>
    /// Records the specified error message and associated exception by adding them as a tuple to the LoggedErrors collection.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="exception">The exception.</param>
    public void LogError(string message, Exception exception) => LoggedErrors.Add((message, exception));

    /// <summary>
    /// Occurs when active document changed.
    /// </summary>
    public event EventHandler? ActiveDocumentChanged;

    /// <summary>
    /// Occurs when workspace root changed.
    /// </summary>
    public event EventHandler? WorkspaceRootChanged;

    /// <summary>
    /// Sets the active document state and raises the ActiveDocumentChanged event to notify subscribers of the change.
    /// </summary>
    /// <param name="value">The value.</param>
    public void SetHasActiveDocument(bool value)
    {
        HasActiveDocument = value;
        ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Mirrors the host opening or closing a solution: the root changes first, then the
    /// notification follows.</summary>
    public void SetWorkspaceRoot(string? value)
    {
        _workspaceRoot = value;
        WorkspaceRootChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets or sets the capture handler.
    /// </summary>
    public Func<CancellationToken, Task<EditorDocumentSnapshot?>>? CaptureHandler { get; set; }

    /// <summary>
    /// Asynchronously captures a snapshot of the currently active editor document by delegating to the configured capture handler, or returns null when no handler is registered.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the editor document snapshot?.</returns>
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

    /// <summary>Injects a failure for the real host's <c>VS.Documents.OpenAsync</c>, which can fail
    /// for a deleted, renamed or locked document.</summary>
    public Func<string, int?, CancellationToken, Task>? OpenDocumentHandler { get; set; }

    /// <summary>
    /// Records the requested document path and optional line number, then asynchronously invokes the registered open-document handler, or completes immediately if no handler is configured.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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

    /// <summary>
    /// Asynchronously attempts to read the text of an open document at the specified path, delegating to a custom read handler when one is configured and otherwise returning the cached content or null if the document is not open.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the string?.</returns>
    public Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (ReadOpenDocumentHandler is not null)
        {
            return ReadOpenDocumentHandler(path, cancellationToken);
        }

        return Task.FromResult(OpenDocuments.TryGetValue(path, out var text) ? text : null);
    }

    /// <summary>
    /// Attempts to update the in-memory text of an open document at the specified path, delegating to a registered write handler when one is configured, and returns a task that resolves to true if the document was open and updated or false otherwise.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
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

    /// <summary>Injects a failure for the VSIX host's modal confirmation dialog.</summary>
    public Func<CancellationToken, Task<bool>>? ConfirmSignOutHandler { get; set; }

    /// <summary>
    /// Records the cancellation token in the sign-out request history and asynchronously confirms a sign-out-everywhere request by invoking the configured handler, or returns the default response when no handler is set.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public Task<bool> ConfirmSignOutEverywhereAsync(CancellationToken cancellationToken)
    {
        ConfirmSignOutRequests.Add(cancellationToken);

        return ConfirmSignOutHandler?.Invoke(cancellationToken) ?? Task.FromResult(ConfirmSignOutResponse);
    }

    /// <summary>
    /// Gets or sets the last copied text.
    /// </summary>
    public string? LastCopiedText { get; private set; }

    /// <summary>
    /// Copies the specified text to the clipboard by storing it in the LastCopiedText property.
    /// </summary>
    /// <param name="text">The text.</param>
    public void CopyToClipboard(string text) => LastCopiedText = text;
}
