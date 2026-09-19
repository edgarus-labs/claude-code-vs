using ClaudeCode.Acp;
using ClaudeCode.Contracts;
using ClaudeCode.Vsix.Options;
using ClaudeCode.Vsix.VsControl;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
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
        var workingDirectory = _workingDirectoryProvider();
        var (fileName, arguments, environment) = WrapWithVisualStudioLauncher(resolved);

        // Spawning the ACP adapter process must not run on the UI thread; hop to the thread pool first.
        var connection = await Task.Run(async () =>
        {
            var inner = new AcpProcessConnectionFactory(fileName, arguments, workingDirectory, environment);
            return await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new VsControlInjectingConnection(connection, _vsControlSessionRegistry);
    }

    /// <summary>When the adapter resolved to `node <pkg>/dist/index.js`, run our bundled launcher
    /// (Resources\Scripts\claude-acp-vs.mjs) instead: it adds the Remote Control extension request on
    /// top of the unchanged adapter. Any other executable shape (a custom wrapper, a native build) is
    /// used as-is and simply has no Remote Control.</summary>
    internal static (string FileName, IReadOnlyList<string>? Arguments, IReadOnlyDictionary<string, string>? Environment) WrapWithVisualStudioLauncher(AcpExecutableSpec resolved)
    {
        var entry = resolved.Arguments is { Count: 1 } ? resolved.Arguments[0] : null;
        var normalized = entry?.Replace('\\', '/');
        if (normalized is null || !normalized.EndsWith("/@agentclientprotocol/claude-agent-acp/dist/index.js", StringComparison.OrdinalIgnoreCase))
        {
            return (resolved.FileName, resolved.Arguments, null);
        }

        var launcher = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(ClaudeCodeConnectionFactory).Assembly.Location) ?? string.Empty,
            "Resources", "Scripts", "claude-acp-vs.mjs");
        if (!System.IO.File.Exists(launcher))
        {
            return (resolved.FileName, resolved.Arguments, null);
        }

        var packageDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(entry!))!;
        return (resolved.FileName, new[] { launcher }, new Dictionary<string, string> { ["CLAUDE_ACP_ADAPTER_DIR"] = packageDir });
    }
}
