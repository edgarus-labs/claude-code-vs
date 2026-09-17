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
    public List<(string Id, string Value)> ConfigChanges { get; } = [];
    public int CancelCount { get; private set; }
    public int DisposeCount { get; private set; }
    public bool IsInitialized { get; private set; }
    public IReadOnlyList<SessionConfigOption> ConfigOptions { get; set; } = [];
    public Func<CancellationToken, Task>? InitializeHandler { get; set; }
    public Func<CancellationToken, Task<NewSessionResult>>? NewSessionHandler { get; set; }
    public Func<string, string, CancellationToken, Task<IReadOnlyList<SessionConfigOption>>>? ConfigHandler { get; set; }
    public Func<IReadOnlyList<ContentBlock>, Task>? PromptHandler { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsInitialized = true;
        return InitializeHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task<NewSessionResult> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken) =>
        NewSessionHandler?.Invoke(cancellationToken) ?? Task.FromResult(new NewSessionResult(SessionId, ConfigOptions));

    public Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken)
    {
        ConfigChanges.Add((configId, value));
        return ConfigHandler?.Invoke(configId, value, cancellationToken) ?? Task.FromResult(ConfigOptions);
    }

    public Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken)
    {
        Prompts.Add(content);
        return PromptHandler?.Invoke(content) ?? Task.CompletedTask;
    }

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        CancelCount++;
        return Task.CompletedTask;
    }

    public event EventHandler<SessionUpdateEventArgs>? SessionUpdate;
    public event EventHandler<PermissionRequestEventArgs>? PermissionRequested;
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

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return default;
    }
}
