using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>
/// Defines a contract for creating asynchronous connections to ACP agents.
/// </summary>
public interface IAcpAgentConnectionFactory
{
    Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken);
}
