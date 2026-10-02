using ClaudeCode.Contracts;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Acp;

/// <summary>
/// Represents a factory that establishes connections to an ACp agent process using a specified executable, arguments, working directory, and environment variables.
/// </summary>
public sealed class AcpProcessConnectionFactory : IAcpAgentConnectionFactory
{
    private readonly string _executableFileName;
    private readonly IReadOnlyList<string>? _arguments;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string>? _environmentVariables;

    /// <summary>
    /// Initializes a new instance of the AcpProcessConnectionFactory class with the specified executable file name, optional arguments, working directory, and environment variables.
    /// </summary>
    /// <param name="executableFileName">The executable file name.</param>
    /// <param name="arguments">The collection of arguments.</param>
    /// <param name="workingDirectory">The working directory.</param>
    /// <param name="environmentVariables">The environment variables.</param>
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

    /// <summary>
    /// Asynchronously creates and initializes an AcpProcessConnection using the configured executable, arguments, working directory, and environment variables, returning the resulting IAcpAgentConnection.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the iacp agent connection.</returns>
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
