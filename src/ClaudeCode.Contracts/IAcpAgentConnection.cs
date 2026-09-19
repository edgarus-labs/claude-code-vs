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
    /// Every field of every returned <see cref="SessionSummary"/> is agent-reported and therefore
    /// untrusted; see <see cref="SessionSummary.Cwd"/> in particular.
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes a previously recorded session identified by <paramref name="sessionId"/>, rooted at
    /// <paramref name="cwd"/>. The agent replays the session's prior history as ordinary
    /// <see cref="SessionUpdate"/> notifications through <see cref="SessionUpdate"/> before this call
    /// returns.
    /// <para><paramref name="cwd"/> is sent to the remote agent process as-is - implementations do not
    /// validate, canonicalize, or sandbox it in any way, and a host MUST NOT adopt it as the root of its
    /// own workspace sandbox. The caller MUST pass only a path it already trusts (its own workspace
    /// root, or one already checked against a workspace boundary). In particular it MUST NOT pass
    /// <see cref="SessionSummary.Cwd"/> straight back: that value is agent-supplied, so doing so lets
    /// the agent choose the directory the client confines itself to.</para>
    /// </summary>
    Task<NewSessionResult> LoadSessionAsync(string sessionId, string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken);

    Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken);

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
