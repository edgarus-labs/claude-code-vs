using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;

namespace ClaudeCode.Core.ViewModels.Demo
{
    public sealed class FakeAcpAgentConnectionFactory : IAcpAgentConnectionFactory
    {
        public Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IAcpAgentConnection>(new FakeAcpAgentConnection(TimeSpan.FromMilliseconds(15)));
    }
}
