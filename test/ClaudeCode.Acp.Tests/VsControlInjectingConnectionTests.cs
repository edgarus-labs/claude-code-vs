using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// Pins the two host-security properties of <see cref="VsControlInjectingConnection"/>: the sandbox
/// root handed to a VS-control server is the host's, never the wire-supplied <c>cwd</c>; and at most
/// one control server is live per connection, so an abandoned session cannot keep driving the IDE.
/// </summary>
public sealed class VsControlInjectingConnectionTests
{
    [Fact]
    public async Task NewSessionAsync_WhenASecondSessionStarts_EndsTheSupersededSessionsControlServer()
    {
        var host = new RecordingSessionHost();
        await using var connection = new VsControlInjectingConnection(new StubConnection(), host, () => @"C:\host\solution");

        await connection.NewSessionAsync(@"C:\agent\cwd", null, CancellationToken.None);
        await connection.NewSessionAsync(@"C:\agent\cwd", null, CancellationToken.None);

        Assert.Equal(new[] { host.Started[0] }, host.Ended);
        Assert.Equal(new[] { host.Started[1] }, host.Live);
    }

    [Fact]
    public async Task LoadSessionAsync_WhenItSupersedesANewSession_EndsTheSupersededSessionsControlServer()
    {
        var host = new RecordingSessionHost();
        await using var connection = new VsControlInjectingConnection(new StubConnection(), host, () => @"C:\host\solution");

        await connection.NewSessionAsync(@"C:\agent\cwd", null, CancellationToken.None);
        await connection.LoadSessionAsync("resumed", @"C:\agent\cwd", null, CancellationToken.None);

        Assert.Equal(new[] { host.Started[0] }, host.Ended);
        Assert.Equal(new[] { host.Started[1] }, host.Live);
    }

    [Fact]
    public async Task LoadSessionAsync_WhenTheAgentRejectsTheResume_EndsOnlyTheServerItJustStarted()
    {
        var host = new RecordingSessionHost();
        var inner = new StubConnection();
        await using var connection = new VsControlInjectingConnection(inner, host, () => @"C:\host\solution");
        await connection.NewSessionAsync(@"C:\agent\cwd", null, CancellationToken.None);
        inner.LoadFailure = new InvalidOperationException("no such session");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.LoadSessionAsync("gone", @"C:\agent\cwd", null, CancellationToken.None));

        // The session the user is still in must survive a failed resume; only the stillborn one dies.
        Assert.Equal(new[] { host.Started[1] }, host.Ended);
        Assert.Equal(new[] { host.Started[0] }, host.Live);
    }

    [Fact]
    public async Task NewSessionAsync_WhenTheAgentRejectsTheNewSession_EndsOnlyTheServerItJustStarted()
    {
        var host = new RecordingSessionHost();
        var inner = new StubConnection();
        await using var connection = new VsControlInjectingConnection(inner, host, () => @"C:\host\solution");
        await connection.NewSessionAsync(@"C:\agent\cwd", null, CancellationToken.None);
        inner.NewFailure = new InvalidOperationException("agent refused");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.NewSessionAsync(@"C:\agent\cwd", null, CancellationToken.None));

        Assert.Equal(new[] { host.Started[1] }, host.Ended);
        Assert.Equal(new[] { host.Started[0] }, host.Live);
    }

    [Fact]
    public async Task DisposeAsync_EndsTheSurvivingControlServerAndDetachesFromTheInnerConnection()
    {
        var host = new RecordingSessionHost();
        var inner = new StubConnection();
        var connection = new VsControlInjectingConnection(inner, host, () => @"C:\host\solution");
        await connection.NewSessionAsync(@"C:\agent\cwd", null, CancellationToken.None);
        await connection.NewSessionAsync(@"C:\agent\cwd", null, CancellationToken.None);

        await connection.DisposeAsync();

        Assert.Empty(host.Live);
        Assert.Equal(host.Started, host.Ended);
        Assert.Equal(0, inner.HandlerCount);
    }

    [Fact]
    public async Task NewSessionAsync_AfterDisposal_LeavesNoControlServerRunning()
    {
        var host = new RecordingSessionHost();
        var connection = new VsControlInjectingConnection(new StubConnection(), host, () => @"C:\host\solution");
        await connection.DisposeAsync();

        await connection.NewSessionAsync(@"C:\agent\cwd", null, CancellationToken.None);

        Assert.Empty(host.Live);
    }

    [Fact]
    public async Task NewSessionAsync_SandboxesTheControlServerToTheHostRootNotTheCallerCwd()
    {
        var host = new RecordingSessionHost();
        await using var connection = new VsControlInjectingConnection(new StubConnection(), host, () => @"C:\host\solution");

        await connection.NewSessionAsync(@"C:\agent\chosen", null, CancellationToken.None);

        Assert.Equal(new[] { @"C:\host\solution" }, host.WorkspaceRoots);
    }

    [Fact]
    public async Task LoadSessionAsync_SandboxesTheControlServerToTheHostRootNotTheAgentReportedCwd()
    {
        var host = new RecordingSessionHost();
        await using var connection = new VsControlInjectingConnection(new StubConnection(), host, () => @"C:\host\solution");

        // A caller could take `cwd` on this path from SessionSummary.Cwd - agent-reported, so honouring
        // it would let the agent pick the directory it is then confined to.
        await connection.LoadSessionAsync("resumed", @"C:\agent\chosen", null, CancellationToken.None);

        Assert.Equal(new[] { @"C:\host\solution" }, host.WorkspaceRoots);
    }

    [Fact]
    public async Task NewSessionAsync_WithNoSolutionOpen_StillSandboxesToTheDenyingNullRoot()
    {
        var host = new RecordingSessionHost();
        await using var connection = new VsControlInjectingConnection(new StubConnection(), host, () => null);

        await connection.NewSessionAsync(@"C:\agent\chosen", null, CancellationToken.None);

        Assert.Equal(new string?[] { null }, host.WorkspaceRoots);
    }

    [Fact]
    public async Task NewSessionAsync_KeepsTheCallersMcpServersAndAppendsTheControlServer()
    {
        var host = new RecordingSessionHost();
        var inner = new StubConnection();
        await using var connection = new VsControlInjectingConnection(inner, host, () => @"C:\host\solution");
        var caller = new McpServerConfig("caller", "caller.exe", Array.Empty<string>());

        await connection.NewSessionAsync(@"C:\agent\cwd", new[] { caller }, CancellationToken.None);

        Assert.Equal(new[] { "caller", "visual-studio" }, inner.LastMcpServers!.Select(s => s.Name));
    }

    [Fact]
    public async Task NewSessionAsync_WhenTheSidecarIsNotDeployed_StartsTheSessionWithoutAControlServer()
    {
        var host = new RecordingSessionHost { IsAvailable = false };
        var inner = new StubConnection();
        await using var connection = new VsControlInjectingConnection(inner, host, () => @"C:\host\solution");

        await connection.NewSessionAsync(@"C:\agent\cwd", null, CancellationToken.None);

        Assert.Empty(inner.LastMcpServers!);
        Assert.Empty(host.Started);
    }

    private sealed class RecordingSessionHost : IVsControlSessionHost
    {
        private int _next;

        public bool IsAvailable { get; set; } = true;

        public List<string> Started { get; } = new List<string>();

        public List<string> Ended { get; } = new List<string>();

        public List<string?> WorkspaceRoots { get; } = new List<string?>();

        public IEnumerable<string> Live => Started.Where(id => !Ended.Contains(id));

        public McpServerConfig StartSession(string? workspaceRoot, out string correlationId)
        {
            correlationId = "session-" + _next++;
            Started.Add(correlationId);
            WorkspaceRoots.Add(workspaceRoot);
            return new McpServerConfig("visual-studio", "vscontrol.exe", new[] { "--pipe", correlationId });
        }

        public void EndSession(string correlationId) => Ended.Add(correlationId);
    }

    private sealed class StubConnection : IAcpAgentConnection
    {
        public Exception? LoadFailure { get; set; }

        public Exception? NewFailure { get; set; }

        public IReadOnlyList<McpServerConfig>? LastMcpServers { get; private set; }

        public bool IsInitialized => true;

        public bool SupportsPromptQueueing => false;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Asynchronously creates a new session in the specified working directory, records the provided MCP server configurations, and returns the initial session result, or throws the preconfigured failure if one is set.
        /// </summary>
        /// <param name="cwd">The cwd.</param>
        /// <param name="mcpServers">The collection of mcp servers.</param>
        /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation. The task result contains the new session result.</returns>
        public Task<NewSessionResult> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
        {
            LastMcpServers = mcpServers;
            if (NewFailure is not null) throw NewFailure;
            return Task.FromResult(new NewSessionResult("new", Array.Empty<SessionConfigOption>()));
        }

        /// <summary>
        /// Asynchronously returns an empty list of session summaries, as no session enumeration is currently implemented for the optional working directory filter.
        /// </summary>
        /// <param name="cwd">The cwd.</param>
        /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation. The task result contains the iread only list.</returns>
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>(Array.Empty<SessionSummary>());

        /// <summary>
        /// Loads a session with the specified identifier and working directory, records the supplied MCP server configurations, and returns a new session result, or throws the configured load failure if one is set.
        /// </summary>
        /// <param name="sessionId">The unique identifier of the session.</param>
        /// <param name="cwd">The cwd.</param>
        /// <param name="mcpServers">The collection of mcp servers.</param>
        /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation. The task result contains the new session result.</returns>
        public Task<NewSessionResult> LoadSessionAsync(string sessionId, string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
        {
            LastMcpServers = mcpServers;
            if (LoadFailure is not null) throw LoadFailure;
            return Task.FromResult(new NewSessionResult(sessionId, Array.Empty<SessionConfigOption>()));
        }

        /// <summary>
        /// Asynchronously sets the value of the specified configuration option for a session and returns the updated session configuration options, currently yielding an empty collection.
        /// </summary>
        /// <param name="sessionId">The unique identifier of the session.</param>
        /// <param name="configId">The unique identifier of the config.</param>
        /// <param name="value">The value.</param>
        /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation. The task result contains the iread only list.</returns>
        public Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SessionConfigOption>>(Array.Empty<SessionConfigOption>());

        /// <summary>
        /// Asynchronously sends the specified content blocks as a prompt to the given session and returns a stop reason of &quot;end_turn&quot; to indicate the turn has completed.
        /// </summary>
        /// <param name="sessionId">The unique identifier of the session.</param>
        /// <param name="content">The collection of content.</param>
        /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation. The task result contains the string.</returns>
        public Task<string> SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken) => Task.FromResult("end_turn");

        /// <summary>
        /// Asynchronously cancels the session identified by the specified session ID; this implementation performs no operation and returns a completed task.
        /// </summary>
        /// <param name="sessionId">The unique identifier of the session.</param>
        /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public Task CancelAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Asynchronously rejects the remote control state change request for the specified session by returning a faulted task with a NotSupportedException, indicating that remote control is not supported by this implementation.
        /// </summary>
        /// <param name="sessionId">The unique identifier of the session.</param>
        /// <param name="enabled">The enabled.</param>
        /// <param name="name">The name.</param>
        /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation. The task result contains the remote control state.</returns>
        public Task<RemoteControlState> SetRemoteControlAsync(string sessionId, bool enabled, string? name, CancellationToken cancellationToken) =>
            Task.FromException<RemoteControlState>(new NotSupportedException());

        /// <summary>Net handlers currently attached across all six events; zero means the decorator detached.</summary>
        public int HandlerCount { get; private set; }

        /// <summary>
        /// Occurs when session update.
        /// </summary>
        public event EventHandler<SessionUpdateEventArgs>? SessionUpdate { add => HandlerCount++; remove => HandlerCount--; }

        /// <summary>
        /// Occurs when permission requested.
        /// </summary>
        public event EventHandler<PermissionRequestEventArgs>? PermissionRequested { add => HandlerCount++; remove => HandlerCount--; }

        /// <summary>
        /// Occurs when elicitation requested.
        /// </summary>
        public event EventHandler<ElicitationRequestEventArgs>? ElicitationRequested { add => HandlerCount++; remove => HandlerCount--; }

        /// <summary>
        /// Occurs when file read requested.
        /// </summary>
        public event EventHandler<FileReadRequestEventArgs>? FileReadRequested { add => HandlerCount++; remove => HandlerCount--; }

        /// <summary>
        /// Occurs when file write requested.
        /// </summary>
        public event EventHandler<FileWriteRequestEventArgs>? FileWriteRequested { add => HandlerCount++; remove => HandlerCount--; }

        /// <summary>
        /// Occurs when disconnected.
        /// </summary>
        public event EventHandler<Exception?>? Disconnected { add => HandlerCount++; remove => HandlerCount--; }

        /// <summary>
        /// Asynchronously releases resources by returning a completed ValueTask without performing any additional cleanup work.
        /// </summary>
        /// <returns>A value task representing the asynchronous operation.</returns>
        public ValueTask DisposeAsync() => default;
    }
}
