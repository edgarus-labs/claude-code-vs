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
        // GetDialogPage (behind _optionsProvider) is UI-thread affine.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var options = _optionsProvider();
        var overridePath = options.CliExecutablePath;

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

        // The adapter and native SDK own credential discovery and refresh, including CLAUDE_CONFIG_DIR.
        var inner = new AcpProcessConnectionFactory(resolved.FileName, resolved.Arguments, _workingDirectoryProvider());
        var connection = await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);

        return new VsControlInjectingConnection(connection, _vsControlSessionRegistry);
    }
}
