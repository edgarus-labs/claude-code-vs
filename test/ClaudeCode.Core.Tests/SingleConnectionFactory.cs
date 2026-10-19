using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

/// <summary>
/// Provides a factory that returns a single, lazily established ACP agent connection, reusing the same instance for all subsequent connect requests.
/// </summary>
internal sealed class SingleConnectionFactory : IAcpAgentConnectionFactory
{
    private readonly IAcpAgentConnection _connection;

    /// <summary>
    /// Initializes a new instance of the SingleConnectionFactory class with the specified ACP agent connection, which is returned for all subsequent connection requests.
    /// </summary>
    /// <param name="connection">The connection.</param>
    public SingleConnectionFactory(IAcpAgentConnection connection) => _connection = connection;

    /// <summary>
    /// Gets or sets the connect handler.
    /// </summary>
    public Func<CancellationToken, Task<IAcpAgentConnection>>? ConnectHandler { get; set; }

    /// <summary>
    /// Asynchronously establishes a connection to the ACP agent by invoking the configured connect handler if one is set, otherwise returning the pre-initialized connection instance.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the iacp agent connection.</returns>
    public Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken) =>
        ConnectHandler?.Invoke(cancellationToken) ?? Task.FromResult(_connection);
}
