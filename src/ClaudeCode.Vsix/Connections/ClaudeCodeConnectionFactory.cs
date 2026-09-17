using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Acp;
using ClaudeCode.Contracts;
using ClaudeCode.Vsix.Auth;
using ClaudeCode.Vsix.Options;
using ClaudeCode.Vsix.VsControl;
using Microsoft.VisualStudio.Shell;
namespace ClaudeCode.Vsix.Connections
{
    /// <summary>
    /// The <see cref="IAcpAgentConnectionFactory"/> stored in <see cref="ClaudeCodeServices.ConnectionFactory"/>.
    /// <c>AcpProcessConnectionFactory</c> (ClaudeCode.Acp) captures its executable path/arguments/environment
    /// variables at construction time and is meant to be recreated per connection attempt, so this wrapper
    /// defers resolving the CLI path, extra arguments, and the current OAuth token until the moment a session
    /// actually connects, constructing a fresh <c>AcpProcessConnectionFactory</c> for every call. The OAuth
    /// token is passed exclusively via the constructor's environment-variable dictionary
    /// (CLAUDE_CODE_OAUTH_TOKEN) - never as a command-line argument, so it never appears in a process list or log.
    /// </summary>
    internal sealed class ClaudeCodeConnectionFactory : IAcpAgentConnectionFactory
    {
        private const string OAuthTokenEnvironmentVariable = "CLAUDE_CODE_OAUTH_TOKEN";

        private readonly Func<ClaudeCodeOptionsPage> _optionsProvider;
        private readonly AcpAuthService _authService;
        private readonly Func<string?> _workingDirectoryProvider;
        private readonly VsControlSessionRegistry _vsControlSessionRegistry;

        public ClaudeCodeConnectionFactory(
            Func<ClaudeCodeOptionsPage> optionsProvider,
            AcpAuthService authService,
            Func<string?> workingDirectoryProvider,
            VsControlSessionRegistry vsControlSessionRegistry)
        {
            _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _workingDirectoryProvider = workingDirectoryProvider ?? throw new ArgumentNullException(nameof(workingDirectoryProvider));
            _vsControlSessionRegistry = vsControlSessionRegistry ?? throw new ArgumentNullException(nameof(vsControlSessionRegistry));
        }

        public async Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            // GetDialogPage (behind _optionsProvider) is UI-thread affine.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var options = _optionsProvider();
            var overridePath = options.CliExecutablePath;

            string fileName;
            IReadOnlyList<string> arguments;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                fileName = overridePath;
                arguments = Array.Empty<string>();
            }
            else
            {
                var resolved = AcpExecutableResolver.TryResolveDefault();
                if (resolved != null)
                {
                    fileName = resolved.FileName;
                    arguments = resolved.Arguments;
                }
                else
                {
                    // Last resort: rely on the OS's own PATH search for a bare executable name.
                    fileName = "claude-code-acp";
                    arguments = Array.Empty<string>();
                }
            }

            var environmentVariables = new Dictionary<string, string>();
            if (_authService.TryGetOauthToken(out var token))
            {
                environmentVariables[OAuthTokenEnvironmentVariable] = token;
            }

            var inner = new AcpProcessConnectionFactory(fileName, arguments, _workingDirectoryProvider(), environmentVariables);
            var connection = await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new VsControlInjectingConnection(connection, _vsControlSessionRegistry);
        }
    }
}
