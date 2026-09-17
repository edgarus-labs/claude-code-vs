using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;

namespace ClaudeCode.Core.Tests
{
    /// <summary>Controllable connection double: <c>SendPromptAsync</c> just records the call, letting tests
    /// raise <see cref="SessionUpdate"/>/<see cref="PermissionRequested"/> events at will afterwards.</summary>
    internal sealed class RecordingAcpAgentConnection : IAcpAgentConnection
    {
        public const string SessionId = "session-1";

        public List<IReadOnlyList<ContentBlock>> Prompts { get; } = new List<IReadOnlyList<ContentBlock>>();

        public int CancelCount { get; private set; }

        public bool IsInitialized { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            IsInitialized = true;
            return Task.CompletedTask;
        }

        public Task<string> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken) =>
            Task.FromResult(SessionId);

        public Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken)
        {
            Prompts.Add(content);
            return Task.CompletedTask;
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

        public void RaiseSessionUpdate(ClaudeCode.Contracts.SessionUpdate update) =>
            SessionUpdate?.Invoke(this, new SessionUpdateEventArgs(SessionId, update));

        public PermissionRequestEventArgs RaisePermissionRequested(ToolCallUpdate call, IReadOnlyList<PermissionOption> options)
        {
            var args = new PermissionRequestEventArgs(SessionId, call, options);
            PermissionRequested?.Invoke(this, args);
            return args;
        }

        public ValueTask DisposeAsync() => default;
    }

    internal sealed class SingleConnectionFactory : IAcpAgentConnectionFactory
    {
        private readonly IAcpAgentConnection _connection;

        public SingleConnectionFactory(IAcpAgentConnection connection) => _connection = connection;

        public Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken) => Task.FromResult(_connection);
    }

    internal sealed class AlwaysSignedInAuthService : IAcpAuthService
    {
        public AuthState CurrentState => AuthState.SignedIn;

        public event EventHandler<AuthStateChangedEventArgs>? StateChanged { add { } remove { } }

        public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) => Task.CompletedTask;

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal sealed class StubChatSessionServices : IChatSessionServices
    {
        public StubChatSessionServices(IAcpAgentConnectionFactory connectionFactory, IAcpAuthService authService, string? workspaceRoot = null)
        {
            ConnectionFactory = connectionFactory;
            AuthService = authService;
            WorkspaceRoot = workspaceRoot;
        }

        public IAcpAgentConnectionFactory ConnectionFactory { get; }

        public IAcpAuthService AuthService { get; }

        public string? WorkspaceRoot { get; }
    }
}
