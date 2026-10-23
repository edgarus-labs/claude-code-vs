using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>
/// Defines a contract for installing and checking readiness of ACP adapters.
/// </summary>
public interface IAcpAdapterInstaller
{
    AdapterReadiness CheckReadiness(string? configuredPath = null);

    Task<string?> GetRuntimeWarningAsync(string? configuredPath = null, CancellationToken cancellationToken = default);

    Task<bool> InstallAdapterAsync(IProgress<string>? progress, CancellationToken cancellationToken);
}
