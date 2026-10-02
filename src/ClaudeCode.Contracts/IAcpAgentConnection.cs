using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

public interface IAcpAgentConnection : IAsyncDisposable
{
    bool IsInitialized { get; }

    /// <summary>True when the agent advertised, in its <c>initialize</c> response, that it accepts a
    /// further <see cref="SendPromptAsync"/> while one is still running and queues it itself until
    /// its next input boundary. False until <see cref="InitializeAsync"/> completes, and whenever the
    /// agent did not advertise it.</summary>
    bool SupportsPromptQueueing { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<NewSessionResult> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken);

    /// <summary>
    /// Lists sessions previously recorded by the agent, optionally filtered to <paramref name="cwd"/>.
    /// Returns only the first page the agent reports. Every field of every returned
    /// <see cref="SessionSummary"/> is agent-reported and untrusted.
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes a previously recorded session identified by <paramref name="sessionId"/>, rooted at
    /// <paramref name="cwd"/>. The agent replays the session's prior history as ordinary
    /// <see cref="SessionUpdate"/> notifications before this call returns.
    /// <para><paramref name="cwd"/> is sent to the agent process as-is, without validation,
    /// canonicalization, or sandboxing. The caller passes only a path it already trusts, never
    /// <see cref="SessionSummary.Cwd"/>.</para>
    /// </summary>
    Task<NewSessionResult> LoadSessionAsync(string sessionId, string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken);

    /// <summary>Runs one prompt turn and returns the agent's <c>stopReason</c> for it once the turn
    /// has ended (<c>"end_turn"</c> when the agent gave none). <c>"cancelled"</c> means the turn was
    /// stopped by <see cref="CancelAsync"/>; for a prompt still waiting in the agent's queue it does
    /// not indicate whether the agent had already folded it into the stopped turn.</summary>
    Task<string> SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken);

    Task CancelAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Turns Remote Control (claude.ai/code) on or off for <paramref name="sessionId"/>.
    /// <paramref name="name"/> labels the session on claude.ai when enabling. Throws when the agent
    /// does not support it.</summary>
    Task<RemoteControlState> SetRemoteControlAsync(string sessionId, bool enabled, string? name, CancellationToken cancellationToken);

    event EventHandler<SessionUpdateEventArgs> SessionUpdate;

    event EventHandler<PermissionRequestEventArgs> PermissionRequested;

    event EventHandler<ElicitationRequestEventArgs> ElicitationRequested;

    event EventHandler<FileReadRequestEventArgs> FileReadRequested;

    event EventHandler<FileWriteRequestEventArgs> FileWriteRequested;

    event EventHandler<Exception?> Disconnected;
}
