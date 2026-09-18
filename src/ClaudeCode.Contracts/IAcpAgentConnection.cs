using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

public interface IAcpAgentConnection : IAsyncDisposable
{
    bool IsInitialized { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<NewSessionResult> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken);

    /// <summary>
    /// Lists sessions previously recorded by the agent, optionally filtered to <paramref name="cwd"/>.
    /// Only the first page the agent returns is surfaced - there is no cursor-based paging here.
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes a previously recorded session identified by <paramref name="sessionId"/>, rooted at
    /// <paramref name="cwd"/>. The agent replays the session's prior history as ordinary
    /// <see cref="SessionUpdate"/> notifications through <see cref="SessionUpdate"/> before this call
    /// returns.
    /// </summary>
    Task<NewSessionResult> LoadSessionAsync(string sessionId, string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken);

    Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken);

    Task CancelAsync(string sessionId, CancellationToken cancellationToken);

    event EventHandler<SessionUpdateEventArgs> SessionUpdate;

    event EventHandler<PermissionRequestEventArgs> PermissionRequested;

    event EventHandler<ElicitationRequestEventArgs> ElicitationRequested;

    event EventHandler<FileReadRequestEventArgs> FileReadRequested;

    event EventHandler<FileWriteRequestEventArgs> FileWriteRequested;

    event EventHandler<Exception?> Disconnected;
}
