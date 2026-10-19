using ClaudeCode.Acp;
using ClaudeCode.Contracts;
using ClaudeCode.Vsix.Options;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix;

/// <summary>
/// Provides a Visual Studio-specific implementation of the ACP adapter installer that checks adapter readiness and installs the adapter by delegating to an inner installer using configured options.
/// </summary>
internal sealed class VsAcpAdapterInstaller : IAcpAdapterInstaller
{
    private readonly AcpAdapterInstaller _inner;
    private readonly Func<ClaudeCodeOptionsPage> _optionsProvider;

    /// <summary>
    /// Initializes a new instance of the VsAcpAdapterInstaller class with the specified options provider, which is validated to be non-null and used to supply the Claude Code options page.
    /// </summary>
    /// <param name="optionsProvider">The options provider.</param>
    /// <exception cref="ArgumentNullException">Thrown when an error occurs during execution.</exception>
    public VsAcpAdapterInstaller(Func<ClaudeCodeOptionsPage> optionsProvider)
    {
        _inner = new ClaudeCode.Acp.AcpAdapterInstaller();
        _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
    }

    /// <summary>
    /// Determines the readiness of the adapter by checking the specified CLI executable path, falling back to the path from the configured options when none is provided.
    /// </summary>
    /// <param name="configuredPath">The configured path.</param>
    /// <returns>The adapter readiness result.</returns>
    public AdapterReadiness CheckReadiness(string? configuredPath = null)
    {
        string? path = configuredPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            try
            {
                path = _optionsProvider().CliExecutablePath;
            }
            catch
            {
            }
        }

        return _inner.CheckReadiness(path);
    }

    /// <summary>
    /// Asynchronously installs the adapter via the inner installer, forwarding each progress line to the caller and mirroring it to the Visual Studio chat session output log with an npm prefix.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public Task<bool> InstallAdapterAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var forwardingProgress = new Progress<string>(line =>
        {
            progress?.Report(line);
            VsChatSessionServices.LogOutputLine("[npm] " + line);
        });

        return _inner.InstallAdapterAsync(forwardingProgress, cancellationToken);
    }
}
