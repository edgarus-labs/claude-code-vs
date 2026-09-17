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

    Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken);

    Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken);

    Task CancelAsync(string sessionId, CancellationToken cancellationToken);

    event EventHandler<SessionUpdateEventArgs> SessionUpdate;

    event EventHandler<PermissionRequestEventArgs> PermissionRequested;

    event EventHandler<FileReadRequestEventArgs> FileReadRequested;

    event EventHandler<FileWriteRequestEventArgs> FileWriteRequested;

    event EventHandler<Exception?> Disconnected;
}
