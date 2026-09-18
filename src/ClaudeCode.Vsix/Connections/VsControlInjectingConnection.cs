using ClaudeCode.Contracts;
using ClaudeCode.Vsix.VsControl;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.Connections;

internal sealed class VsControlInjectingConnection : IAcpAgentConnection
{
    private readonly IAcpAgentConnection _inner;
    private readonly VsControlSessionRegistry _registry;
    private readonly ConcurrentDictionary<string, byte> _correlationIds = new ConcurrentDictionary<string, byte>();
    private int _disposed;

    public VsControlInjectingConnection(IAcpAgentConnection inner, VsControlSessionRegistry registry)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _inner.SessionUpdate += OnSessionUpdate;
        _inner.PermissionRequested += OnPermissionRequested;
        _inner.ElicitationRequested += OnElicitationRequested;
        _inner.FileReadRequested += OnFileReadRequested;
        _inner.FileWriteRequested += OnFileWriteRequested;
        _inner.Disconnected += OnDisconnected;
    }

    public bool IsInitialized => _inner.IsInitialized;

    public Task InitializeAsync(CancellationToken cancellationToken) => _inner.InitializeAsync(cancellationToken);

    public async Task<NewSessionResult> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
    {
        var merged = new List<McpServerConfig>(mcpServers ?? Array.Empty<McpServerConfig>());
        string? correlationId = null;

        if (_registry.IsAvailable)
        {
            merged.Add(_registry.StartSession(cwd, out correlationId));
            _correlationIds.TryAdd(correlationId, 0);
        }
        // else: the VsControlMcp sidecar payload hasn't been built/deployed beside this assembly yet - the
        // session still starts, just without editor/solution tool support, rather than failing outright.

        try
        {
            return await _inner.NewSessionAsync(cwd, merged, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (correlationId is not null && _correlationIds.TryRemove(correlationId, out _))
            {
                _registry.EndSession(correlationId);
            }

            throw;
        }
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken) =>
        _inner.ListSessionsAsync(cwd, cancellationToken);

    public async Task<NewSessionResult> LoadSessionAsync(string sessionId, string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
    {
        var merged = new List<McpServerConfig>(mcpServers ?? Array.Empty<McpServerConfig>());
        string? correlationId = null;

        if (_registry.IsAvailable)
        {
            merged.Add(_registry.StartSession(cwd, out correlationId));
            _correlationIds.TryAdd(correlationId, 0);
        }

        try
        {
            return await _inner.LoadSessionAsync(sessionId, cwd, merged, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (correlationId is not null && _correlationIds.TryRemove(correlationId, out _))
            {
                _registry.EndSession(correlationId);
            }

            throw;
        }
    }

    public Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken) =>
        _inner.SetSessionConfigOptionAsync(sessionId, configId, value, cancellationToken);

    public Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken) =>
        _inner.SendPromptAsync(sessionId, content, cancellationToken);

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken) =>
        _inner.CancelAsync(sessionId, cancellationToken);

    public event EventHandler<SessionUpdateEventArgs>? SessionUpdate;

    public event EventHandler<PermissionRequestEventArgs>? PermissionRequested;

    public event EventHandler<ElicitationRequestEventArgs>? ElicitationRequested;

    public event EventHandler<FileReadRequestEventArgs>? FileReadRequested;

    public event EventHandler<FileWriteRequestEventArgs>? FileWriteRequested;

    public event EventHandler<Exception?>? Disconnected;

    private void OnSessionUpdate(object? sender, SessionUpdateEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0) SessionUpdate?.Invoke(this, e);
    }

    private void OnPermissionRequested(object? sender, PermissionRequestEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0) PermissionRequested?.Invoke(this, e);
    }

    private void OnElicitationRequested(object? sender, ElicitationRequestEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0) ElicitationRequested?.Invoke(this, e);
    }

    private void OnFileReadRequested(object? sender, FileReadRequestEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0) FileReadRequested?.Invoke(this, e);
    }

    private void OnFileWriteRequested(object? sender, FileWriteRequestEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0) FileWriteRequested?.Invoke(this, e);
    }

    private void OnDisconnected(object? sender, Exception? e)
    {
        if (Volatile.Read(ref _disposed) == 0) Disconnected?.Invoke(this, e);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _inner.SessionUpdate -= OnSessionUpdate;
        _inner.PermissionRequested -= OnPermissionRequested;
        _inner.ElicitationRequested -= OnElicitationRequested;
        _inner.FileReadRequested -= OnFileReadRequested;
        _inner.FileWriteRequested -= OnFileWriteRequested;
        _inner.Disconnected -= OnDisconnected;

        foreach (string correlationId in _correlationIds.Keys)
        {
            if (_correlationIds.TryRemove(correlationId, out _))
            {
                _registry.EndSession(correlationId);
            }
        }

        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
