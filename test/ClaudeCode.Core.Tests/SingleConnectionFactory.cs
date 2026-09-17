using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

internal sealed class SingleConnectionFactory : IAcpAgentConnectionFactory
{
    private readonly IAcpAgentConnection _connection;

    public SingleConnectionFactory(IAcpAgentConnection connection) => _connection = connection;

    public Func<CancellationToken, Task<IAcpAgentConnection>>? ConnectHandler { get; set; }

    public Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken) =>
        ConnectHandler?.Invoke(cancellationToken) ?? Task.FromResult(_connection);
}
