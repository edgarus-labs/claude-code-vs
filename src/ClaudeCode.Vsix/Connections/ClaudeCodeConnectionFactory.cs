using ClaudeCode.Acp;
using ClaudeCode.Contracts;
using ClaudeCode.Vsix.Options;
using ClaudeCode.Vsix.VsControl;
using Microsoft.VisualStudio.Shell;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.Connections;

internal sealed class ClaudeCodeConnectionFactory : IAcpAgentConnectionFactory
{
    private readonly Func<ClaudeCodeOptionsPage> _optionsProvider;
    private readonly Func<string?> _workingDirectoryProvider;
    private readonly VsControlSessionRegistry _vsControlSessionRegistry;

    public ClaudeCodeConnectionFactory(
        Func<ClaudeCodeOptionsPage> optionsProvider,
        Func<string?> workingDirectoryProvider,
        VsControlSessionRegistry vsControlSessionRegistry)
    {
        _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        _workingDirectoryProvider = workingDirectoryProvider ?? throw new ArgumentNullException(nameof(workingDirectoryProvider));
        _vsControlSessionRegistry = vsControlSessionRegistry ?? throw new ArgumentNullException(nameof(vsControlSessionRegistry));
    }

    public async Task<IAcpAgentConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        // GetDialogPage (behind _optionsProvider) is UI-thread affine: read the option values here,
        // then leave the thread. The adapter and native SDK own credential discovery and refresh,
        // including CLAUDE_CONFIG_DIR.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var overridePath = _optionsProvider().CliExecutablePath;
        var workingDirectory = _workingDirectoryProvider();

        // Resolving the adapter probes the filesystem (one File.Exists per fully-qualified PATH entry,
        // then the launcher and <package>/dist/acp-agent.js; a single unreachable UNC or mapped-drive
        // entry blocks on an SMB timeout) and spawning it is CreateProcess: none of it may run on the
        // UI thread. Same rule as ClaudeUsageService: hop to the thread pool first.
        var connection = await Task.Run(async () =>
        {
            var resolved = string.IsNullOrWhiteSpace(overridePath)
                ? AcpExecutableResolver.TryResolveDefault()
                : AcpExecutableResolver.TryResolve(overridePath);
            if (resolved is null)
            {
                throw new InvalidOperationException(
                    "The Claude ACP adapter could not be resolved. Install Node.js 22 or newer and run "
                    + "'npm install -g @agentclientprotocol/claude-agent-acp', then restart Visual Studio "
                    + "so it inherits the updated PATH. Alternatively, set Tools > Options > Claude Code > "
                    + "ACP executable path to the installed claude-agent-acp executable, npm shim, or package dist/index.js. "
                    + "The native Claude CLI alone is not an ACP adapter.");
            }

            // Only the assembly-location lookup stays here; the mapping itself lives in ClaudeCode.Acp
            // next to AcpExecutableResolver, where it is unit-tested without a VS host.
            var launcher = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(ClaudeCodeConnectionFactory).Assembly.Location) ?? string.Empty,
                "Resources", "Scripts", "claude-acp-vs.mjs");
            var (fileName, arguments, environment) = AcpLauncherWrap.Wrap(resolved, launcher);

            var inner = new AcpProcessConnectionFactory(fileName, arguments, workingDirectory, environment);
            return await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new VsControlInjectingConnection(connection, _vsControlSessionRegistry, _workingDirectoryProvider);
    }
}
