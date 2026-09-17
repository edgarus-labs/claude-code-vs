using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;
using ClaudeCode.Vsix.VsControl;

namespace ClaudeCode.Vsix.Connections
{
    /// <summary>
    /// Wraps a real <see cref="IAcpAgentConnection"/> so every <see cref="NewSessionAsync"/> call
    /// transparently gets the "visual-studio" MCP server merged into its <c>mcpServers</c> argument -
    /// ClaudeCode.Core's view model calls <c>NewSessionAsync</c> knowing nothing about VS-control pipes,
    /// ClaudeCode.VsControl.Mcp.exe, or its install path.
    /// </summary>
    internal sealed class VsControlInjectingConnection : IAcpAgentConnection
    {
        private readonly IAcpAgentConnection _inner;
        private readonly VsControlSessionRegistry _registry;
        private string? _correlationId;

        public VsControlInjectingConnection(IAcpAgentConnection inner, VsControlSessionRegistry registry)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public bool IsInitialized => _inner.IsInitialized;

        public Task InitializeAsync(CancellationToken cancellationToken) => _inner.InitializeAsync(cancellationToken);

        public async Task<string> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
        {
            var merged = new List<McpServerConfig>(mcpServers ?? Array.Empty<McpServerConfig>());

            if (_registry.IsAvailable)
            {
                merged.Add(_registry.StartSession(out _correlationId));
            }
            // else: ClaudeCode.VsControl.Mcp.exe hasn't been built/deployed next to this assembly yet - the
            // session still starts, just without editor/solution tool support, rather than failing outright.

            return await _inner.NewSessionAsync(cwd, merged, cancellationToken).ConfigureAwait(false);
        }

        public Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken) =>
            _inner.SendPromptAsync(sessionId, content, cancellationToken);

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken) =>
            _inner.CancelAsync(sessionId, cancellationToken);

        public event EventHandler<SessionUpdateEventArgs> SessionUpdate
        {
            add => _inner.SessionUpdate += value;
            remove => _inner.SessionUpdate -= value;
        }

        public event EventHandler<PermissionRequestEventArgs> PermissionRequested
        {
            add => _inner.PermissionRequested += value;
            remove => _inner.PermissionRequested -= value;
        }

        public event EventHandler<FileReadRequestEventArgs> FileReadRequested
        {
            add => _inner.FileReadRequested += value;
            remove => _inner.FileReadRequested -= value;
        }

        public event EventHandler<FileWriteRequestEventArgs> FileWriteRequested
        {
            add => _inner.FileWriteRequested += value;
            remove => _inner.FileWriteRequested -= value;
        }

        public event EventHandler<Exception?> Disconnected
        {
            add => _inner.Disconnected += value;
            remove => _inner.Disconnected -= value;
        }

        public async ValueTask DisposeAsync()
        {
            if (_correlationId != null)
            {
                _registry.EndSession(_correlationId);
                _correlationId = null;
            }

            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
