using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

internal sealed class RecordingAcpAgentConnection : IAcpAgentConnection
{
    public const string SessionId = "session-1";
    public List<IReadOnlyList<ContentBlock>> Prompts { get; } = [];

    public List<string> PromptSessionIds { get; } = [];

    public List<(string Id, string Value)> ConfigChanges { get; } = [];

    public int CancelCount { get; private set; }

    public int DisposeCount { get; private set; }

    public bool IsInitialized { get; private set; }

    public bool SupportsPromptQueueing { get; set; }

    public IReadOnlyList<SessionConfigOption> ConfigOptions { get; set; } = [];

    public Func<CancellationToken, Task>? InitializeHandler { get; set; }

    public Func<CancellationToken, Task<NewSessionResult>>? NewSessionHandler { get; set; }

    public Func<string, string, CancellationToken, Task<IReadOnlyList<SessionConfigOption>>>? ConfigHandler { get; set; }

    public Func<IReadOnlyList<ContentBlock>, Task>? PromptHandler { get; set; }

    public Func<string?, CancellationToken, Task<IReadOnlyList<SessionSummary>>>? ListSessionsHandler { get; set; }

    public Func<string, string, IReadOnlyList<McpServerConfig>?, CancellationToken, Task<NewSessionResult>>? LoadSessionHandler { get; set; }

    public List<string> NewSessionCwds { get; } = [];

    public List<string?> ListSessionsCwds { get; } = [];

    public List<(string SessionId, string Cwd)> LoadedSessions { get; } = [];

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsInitialized = true;

        return InitializeHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task<NewSessionResult> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
    {
        NewSessionCwds.Add(cwd);

        return NewSessionHandler?.Invoke(cancellationToken) ?? Task.FromResult(new NewSessionResult(SessionId, ConfigOptions));
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken)
    {
        ListSessionsCwds.Add(cwd);

        return ListSessionsHandler?.Invoke(cwd, cancellationToken) ?? Task.FromResult<IReadOnlyList<SessionSummary>>([]);
    }

    public Task<NewSessionResult> LoadSessionAsync(string sessionId, string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
    {
        LoadedSessions.Add((sessionId, cwd));

        return LoadSessionHandler?.Invoke(sessionId, cwd, mcpServers, cancellationToken) ?? Task.FromResult(new NewSessionResult(sessionId, ConfigOptions));
    }

    public Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken)
    {
        ConfigChanges.Add((configId, value));

        return ConfigHandler?.Invoke(configId, value, cancellationToken) ?? Task.FromResult(ConfigOptions);
    }

    // A handler returning Task<string> supplies the stop reason; any other Task (or a null reason)
    // ends as "end_turn". Like AcpProcessConnection, TurnEnded is raised just before returning.
    public async Task<string> SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken)
    {
        Prompts.Add(content);
        PromptSessionIds.Add(sessionId);
        var turn = PromptHandler?.Invoke(content) ?? Task.CompletedTask;
        await turn;
        var stopReason = (turn as Task<string>)?.Result ?? "end_turn";
        RaiseSessionUpdate(new SessionUpdate.TurnEnded(stopReason), sessionId);

        return stopReason;
    }

    public Func<Task>? CancelHandler { get; set; }

    /// <summary>
    /// Increments the cancellation counter and asynchronously invokes the configured cancel handler, if one is set, otherwise completing immediately.
    /// </summary>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        CancelCount++;

        return CancelHandler?.Invoke() ?? Task.CompletedTask;
    }

    /// <summary>
    /// Gets the collection of remote control calls.
    /// </summary>
    public List<(string SessionId, bool Enabled, string? Name)> RemoteControlCalls { get; } = [];

    /// <summary>
    /// Gets or sets the remote control handler.
    /// </summary>
    public Func<bool, Task<RemoteControlState>>? RemoteControlHandler { get; set; }

    /// <summary>
    /// Asynchronously enables or disables remote control for the specified session, recording the request and returning the resulting state, which includes a session URL when remote control is enabled.
    /// </summary>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <param name="enabled">The enabled.</param>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the remote control state.</returns>
    public Task<RemoteControlState> SetRemoteControlAsync(string sessionId, bool enabled, string? name, CancellationToken cancellationToken)
    {
        RemoteControlCalls.Add((sessionId, enabled, name));

        return RemoteControlHandler?.Invoke(enabled)
            ?? Task.FromResult(new RemoteControlState(enabled, enabled ? "https://claude.ai/code/session/test" : null));
    }

    /// <summary>
    /// Occurs when session update.
    /// </summary>
    public event EventHandler<SessionUpdateEventArgs>? SessionUpdate;

    /// <summary>
    /// Occurs when permission requested.
    /// </summary>
    public event EventHandler<PermissionRequestEventArgs>? PermissionRequested;

    /// <summary>
    /// Occurs when elicitation requested.
    /// </summary>
    public event EventHandler<ElicitationRequestEventArgs>? ElicitationRequested;

    /// <summary>
    /// Occurs when file read requested.
    /// </summary>
    public event EventHandler<FileReadRequestEventArgs>? FileReadRequested;

    /// <summary>
    /// Occurs when file write requested.
    /// </summary>
    public event EventHandler<FileWriteRequestEventArgs>? FileWriteRequested;

    /// <summary>
    /// Occurs when disconnected.
    /// </summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>
    /// Raises the SessionUpdate event with the specified update and session identifier, defaulting to the current session&apos;s identifier when none is provided.
    /// </summary>
    /// <param name="update">The update.</param>
    /// <param name="sessionId">The unique identifier of the session.</param>
    public void RaiseSessionUpdate(SessionUpdate update, string? sessionId = null) =>
        SessionUpdate?.Invoke(this, new SessionUpdateEventArgs(sessionId ?? SessionId, update));

    /// <summary>
    /// Raises the Disconnected event to notify subscribers that the connection has been lost or closed.
    /// </summary>
    public void RaiseDisconnected() => Disconnected?.Invoke(this, null);

    /// <summary>
    /// Raises the PermissionRequested event with the specified tool call and permission options, and returns the event arguments so subscribers&apos; decisions can be read back by the caller.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="options">The collection of options.</param>
    /// <returns>The permission request event args result.</returns>
    public PermissionRequestEventArgs RaisePermissionRequested(ToolCallUpdate call, IReadOnlyList<PermissionOption> options)
    {
        var args = new PermissionRequestEventArgs(SessionId, call, options);
        PermissionRequested?.Invoke(this, args);

        return args;
    }

    /// <summary>
    /// Raises the ElicitationRequested event for the current session with the specified message and form fields, and returns the event arguments so the caller can inspect the response provided by subscribers.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="fields">The collection of fields.</param>
    /// <returns>The elicitation request event args result.</returns>
    public ElicitationRequestEventArgs RaiseElicitationRequested(string message, IReadOnlyList<ElicitationField> fields)
    {
        var args = new ElicitationRequestEventArgs(SessionId, message, fields);
        ElicitationRequested?.Invoke(this, args);

        return args;
    }

    /// <summary>
    /// Raises the FileReadRequested event for the specified file path, optionally scoped to a line and limit, and returns the event arguments so subscribers can supply the file content.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="line">The line.</param>
    /// <param name="limit">The limit.</param>
    /// <returns>The file read request event args result.</returns>
    public FileReadRequestEventArgs RaiseFileReadRequested(string path, int? line = null, int? limit = null)
    {
        var args = new FileReadRequestEventArgs(path, line, limit);
        FileReadRequested?.Invoke(this, args);

        return args;
    }

    /// <summary>
    /// Raises the FileWriteRequested event for the specified path and content, allowing subscribers to inspect or cancel the write, and returns the event arguments so the caller can evaluate the outcome.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="content">The content.</param>
    /// <returns>The file write request event args result.</returns>
    public FileWriteRequestEventArgs RaiseFileWriteRequested(string path, string content)
    {
        var args = new FileWriteRequestEventArgs(path, content);
        FileWriteRequested?.Invoke(this, args);

        return args;
    }

    /// <summary>Holds <see cref="DisposeAsync"/> open so a test can observe the view model's state
    /// while an agent teardown is still in flight.</summary>
    public Func<Task>? DisposeHandler { get; set; }

    /// <summary>
    /// Asynchronously disposes the instance by incrementing the dispose counter and invoking the optional dispose handler, if one has been configured.
    /// </summary>
    /// <returns>A value task representing the asynchronous operation.</returns>
    public ValueTask DisposeAsync()
    {
        DisposeCount++;

        return DisposeHandler is null ? default : new ValueTask(DisposeHandler());
    }
}
