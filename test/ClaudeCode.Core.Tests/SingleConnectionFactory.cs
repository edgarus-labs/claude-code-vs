using ClaudeCode.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

internal sealed class SingleConnectionFactory : IAcpAgentConnectionFactory
{
    private readonly IAcpAgentConnection _connection;

    public SingleConnectionFactory(IAcpAgentConnection connection) => _connection = connection;

    /// <summary>
    /// Gets or sets the connect handler.
    /// </summary>
    public Func<CancellationToken, Task<IAcpAgentConnection>>? ConnectHandler { get; set; }

    public Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken) =>
        ConnectHandler?.Invoke(cancellationToken) ?? Task.FromResult(_connection);
}
