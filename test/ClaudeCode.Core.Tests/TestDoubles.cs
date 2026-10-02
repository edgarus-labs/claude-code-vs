using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

internal sealed class RecordingAcpAgentConnection : IAcpAgentConnection
{
    /// <summary>
    /// The session id.
    /// </summary>
    public const string SessionId = "session-1";
    /// <summary>
    /// Gets the collection of prompts.
    /// </summary>
    public List<IReadOnlyList<ContentBlock>> Prompts { get; } = [];
    /// <summary>
    /// Gets the collection of prompt session ids.
    /// </summary>
    public List<string> PromptSessionIds { get; } = [];
    /// <summary>
    /// Gets the collection of config changes.
    /// </summary>
    public List<(string Id, string Value)> ConfigChanges { get; } = [];
    /// <summary>
    /// Gets or sets the cancel count.
    /// </summary>
    public int CancelCount { get; private set; }
    /// <summary>
    /// Gets or sets the dispose count.
    /// </summary>
    public int DisposeCount { get; private set; }
    /// <summary>
    /// Gets or sets a value indicating whether is initialized.
    /// </summary>
    public bool IsInitialized { get; private set; }
    /// <summary>
    /// Gets or sets a value indicating whether supports prompt queueing.
    /// </summary>
    public bool SupportsPromptQueueing { get; set; }
    /// <summary>
    /// Gets or sets the collection of config options.
    /// </summary>
    public IReadOnlyList<SessionConfigOption> ConfigOptions { get; set; } = [];
    /// <summary>
    /// Gets or sets the initialize handler.
    /// </summary>
    public Func<CancellationToken, Task>? InitializeHandler { get; set; }
    /// <summary>
    /// Gets or sets the new session handler.
    /// </summary>
    public Func<CancellationToken, Task<NewSessionResult>>? NewSessionHandler { get; set; }
    /// <summary>
    /// Gets or sets the config handler.
    /// </summary>
    public Func<string, string, CancellationToken, Task<IReadOnlyList<SessionConfigOption>>>? ConfigHandler { get; set; }
    /// <summary>
    /// Gets or sets the prompt handler.
    /// </summary>
    public Func<IReadOnlyList<ContentBlock>, Task>? PromptHandler { get; set; }
    /// <summary>
    /// Gets or sets the list sessions handler.
    /// </summary>
    public Func<string?, CancellationToken, Task<IReadOnlyList<SessionSummary>>>? ListSessionsHandler { get; set; }
    /// <summary>
    /// Gets or sets the load session handler.
    /// </summary>
    public Func<string, string, IReadOnlyList<McpServerConfig>?, CancellationToken, Task<NewSessionResult>>? LoadSessionHandler { get; set; }
    /// <summary>
    /// Gets the collection of new session cwds.
    /// </summary>
    public List<string> NewSessionCwds { get; } = [];
    /// <summary>
    /// Gets the collection of list sessions cwds.
    /// </summary>
    public List<string?> ListSessionsCwds { get; } = [];
    /// <summary>
    /// Gets the collection of loaded sessions.
    /// </summary>
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

    /// <summary>
    /// Gets or sets the cancel handler.
    /// </summary>
    public Func<Task>? CancelHandler { get; set; }

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

    public void RaiseSessionUpdate(SessionUpdate update, string? sessionId = null) =>
        SessionUpdate?.Invoke(this, new SessionUpdateEventArgs(sessionId ?? SessionId, update));

    public void RaiseDisconnected() => Disconnected?.Invoke(this, null);

    public PermissionRequestEventArgs RaisePermissionRequested(ToolCallUpdate call, IReadOnlyList<PermissionOption> options)
    {
        var args = new PermissionRequestEventArgs(SessionId, call, options);
        PermissionRequested?.Invoke(this, args);
        return args;
    }

    public ElicitationRequestEventArgs RaiseElicitationRequested(string message, IReadOnlyList<ElicitationField> fields)
    {
        var args = new ElicitationRequestEventArgs(SessionId, message, fields);
        ElicitationRequested?.Invoke(this, args);
        return args;
    }

    public FileReadRequestEventArgs RaiseFileReadRequested(string path, int? line = null, int? limit = null)
    {
        var args = new FileReadRequestEventArgs(path, line, limit);
        FileReadRequested?.Invoke(this, args);
        return args;
    }

    public FileWriteRequestEventArgs RaiseFileWriteRequested(string path, string content)
    {
        var args = new FileWriteRequestEventArgs(path, content);
        FileWriteRequested?.Invoke(this, args);
        return args;
    }

    /// <summary>
    /// Gets or sets the dispose handler.
    /// </summary>
    public Func<Task>? DisposeHandler { get; set; }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return DisposeHandler is null ? default : new ValueTask(DisposeHandler());
    }
}
