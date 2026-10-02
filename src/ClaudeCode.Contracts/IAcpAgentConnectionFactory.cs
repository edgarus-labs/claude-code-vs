using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

public interface IAcpAgentConnectionFactory
{
    Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken);
}
