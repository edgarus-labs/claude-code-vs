using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;

namespace ClaudeCode.Acp
{
    /// <summary>
    /// Owns the child-process command line for the ACP agent (executable + args, e.g. resolved from
    /// <see cref="AcpExecutableResolver"/> or VS Options) and spins up a fresh, already-`initialize`d
    /// <see cref="AcpProcessConnection"/> per call to <see cref="ConnectAsync"/>.
    /// </summary>
    public sealed class AcpProcessConnectionFactory : IAcpAgentConnectionFactory
    {
        private readonly string _executableFileName;
        private readonly IReadOnlyList<string>? _arguments;
        private readonly string? _workingDirectory;
        private readonly IReadOnlyDictionary<string, string>? _environmentVariables;

        /// <param name="executableFileName">Absolute path or bare command name (resolved via PATH) of the
        /// ACP agent executable, e.g. `claude-code-acp` or `claude` (with `--acp` folded into <paramref name="arguments"/>).</param>
        /// <param name="arguments">Extra command-line arguments, e.g. `["--acp"]` for the `claude` CLI fallback or a `--model` override.</param>
        /// <param name="workingDirectory">Working directory for the spawned process itself. Independent of the ACP
        /// session `cwd` passed later to <see cref="IAcpAgentConnection.NewSessionAsync"/>.</param>
        /// <param name="environmentVariables">Extra/overriding environment variables (e.g. CLAUDE_CODE_OAUTH_TOKEN).
        /// Merged on top of the spawned process's normally-inherited environment - never replaces it.</param>
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
}
