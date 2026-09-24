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

    public Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken)
    {
        Prompts.Add(content);
        PromptSessionIds.Add(sessionId);
        return PromptHandler?.Invoke(content) ?? Task.CompletedTask;
    }

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        CancelCount++;
        return Task.CompletedTask;
    }

    public List<(string SessionId, bool Enabled, string? Name)> RemoteControlCalls { get; } = [];
    public Func<bool, Task<RemoteControlState>>? RemoteControlHandler { get; set; }

    public Task<RemoteControlState> SetRemoteControlAsync(string sessionId, bool enabled, string? name, CancellationToken cancellationToken)
    {
        RemoteControlCalls.Add((sessionId, enabled, name));
        return RemoteControlHandler?.Invoke(enabled)
            ?? Task.FromResult(new RemoteControlState(enabled, enabled ? "https://claude.ai/code/session/test" : null));
    }

    public event EventHandler<SessionUpdateEventArgs>? SessionUpdate;
    public event EventHandler<PermissionRequestEventArgs>? PermissionRequested;
    public event EventHandler<ElicitationRequestEventArgs>? ElicitationRequested;
    public event EventHandler<FileReadRequestEventArgs>? FileReadRequested;
    public event EventHandler<FileWriteRequestEventArgs>? FileWriteRequested;
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

    /// <summary>Holds <see cref="DisposeAsync"/> open so a test can observe the view model's state
    /// while an agent teardown is still in flight.</summary>
    public Func<Task>? DisposeHandler { get; set; }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return DisposeHandler is null ? default : new ValueTask(DisposeHandler());
    }
}
