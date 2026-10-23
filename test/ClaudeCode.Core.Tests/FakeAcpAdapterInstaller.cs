using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

/// <summary>
/// Provides a fake implementation of the ACP adapter installer for testing, allowing configurable readiness state and install behavior while tracking the number of installation attempts.
/// </summary>
internal sealed class FakeAcpAdapterInstaller : IAcpAdapterInstaller
{
    /// <summary>
    /// Gets or sets the current readiness.
    /// </summary>
    public AdapterReadiness CurrentReadiness { get; set; } = AdapterReadiness.Ready;

    /// <summary>
    /// Gets or sets the install handler.
    /// </summary>
    public Func<IProgress<string>?, CancellationToken, Task<bool>>? InstallHandler { get; set; }

    /// <summary>
    /// Gets or sets the install count.
    /// </summary>
    public int InstallCount { get; private set; }

    public AdapterReadiness CheckReadiness(string? configuredPath = null) => CurrentReadiness;

    public string? RuntimeWarning { get; set; }

    public Task<string?> GetRuntimeWarningAsync(string? configuredPath = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(RuntimeWarning);

    /// <summary>
    /// Asynchronously installs the adapter by invoking the configured install handler, increments the install counter, and returns true when no handler is configured.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public Task<bool> InstallAdapterAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        InstallCount++;

        return InstallHandler?.Invoke(progress, cancellationToken) ?? Task.FromResult(true);
    }
}
