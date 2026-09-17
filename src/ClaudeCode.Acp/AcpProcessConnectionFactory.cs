using ClaudeCode.Contracts;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Acp;

public sealed class AcpProcessConnectionFactory : IAcpAgentConnectionFactory
{
    private readonly string _executableFileName;
    private readonly IReadOnlyList<string>? _arguments;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string>? _environmentVariables;

    public AcpProcessConnectionFactory(
        string executableFileName,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        _executableFileName = executableFileName;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
        _environmentVariables = environmentVariables;
    }

    public async Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = new AcpProcessConnection(_executableFileName, _arguments, _workingDirectory, _environmentVariables);
        try
        {
            await connection.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        return connection;
    }
}
