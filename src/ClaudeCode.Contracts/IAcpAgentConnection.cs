using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts
{
    /// <summary>
    /// Live connection to one spawned agent process (`claude-code-acp`) speaking the Agent Client Protocol
    /// (JSON-RPC 2.0 over stdio). Implemented by ClaudeCode.Acp; consumed by ClaudeCode.Core view models
    /// and by ClaudeCode.Vsix, which owns the process lifetime.
    /// </summary>
    public interface IAcpAgentConnection : IAsyncDisposable
    {
        /// <summary>True once `initialize` has completed successfully.</summary>
        bool IsInitialized { get; }

        Task InitializeAsync(CancellationToken cancellationToken);

        /// <summary>Starts a new logical conversation rooted at <paramref name="cwd"/>, optionally wiring in
        /// client-side MCP servers (e.g. the "visual-studio" control server); returns the sessionId.</summary>
        Task<string> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken);

        /// <summary>Submits one user turn; streams results via <see cref="SessionUpdate"/> until a TurnEnded update fires.</summary>
        Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken);

        Task CancelAsync(string sessionId, CancellationToken cancellationToken);

        event EventHandler<SessionUpdateEventArgs> SessionUpdate;

        /// <summary>Agent -> client: ask the user for consent before a side-effecting tool call.</summary>
        event EventHandler<PermissionRequestEventArgs> PermissionRequested;

        /// <summary>Agent -> client: read a file from the editor's workspace (respects unsaved buffers).</summary>
        event EventHandler<FileReadRequestEventArgs> FileReadRequested;

        /// <summary>Agent -> client: write/patch a file in the editor's workspace.</summary>
        event EventHandler<FileWriteRequestEventArgs> FileWriteRequested;

        /// <summary>Raised when the underlying process exits unexpectedly.</summary>
        event EventHandler<Exception?> Disconnected;
    }

    /// <summary>Creates connections and owns the child-process command line (executable path, model, extra args).</summary>
    public interface IAcpAgentConnectionFactory
    {
        Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Wraps the Claude Code CLI's own OAuth device/browser login (`claude setup-token`) so the extension
    /// never handles API keys: it captures the resulting long-lived OAuth token and stores it in the OS
    /// credential store, then exports it as CLAUDE_CODE_OAUTH_TOKEN for the agent child process.
    /// </summary>
    public interface IAcpAuthService
    {
        AuthState CurrentState { get; }

        event EventHandler<AuthStateChangedEventArgs> StateChanged;

        Task<bool> IsSignedInAsync(CancellationToken cancellationToken);

        /// <summary>Runs the CLI's OAuth browser flow and persists the resulting token. Throws on failure/cancel.</summary>
        Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null);

        Task SignOutAsync(CancellationToken cancellationToken);
    }
}
