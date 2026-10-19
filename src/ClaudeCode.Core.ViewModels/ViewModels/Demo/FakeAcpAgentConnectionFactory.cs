using ClaudeCode.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels.Demo;

/// <summary>
/// Provides a fake implementation of the ACP agent connection factory for testing scenarios that require simulated agent connections without a live transport.
/// </summary>
public sealed class FakeAcpAgentConnectionFactory : IAcpAgentConnectionFactory
{
    /// <summary>
    /// Asynchronously returns a fake ACP agent connection with a simulated 15-millisecond response delay for testing purposes.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the iacp agent connection.</returns>
    public Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IAcpAgentConnection>(new FakeAcpAgentConnection(TimeSpan.FromMilliseconds(15)));
}
