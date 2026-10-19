using ClaudeCode.Contracts;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels.Demo;

/// <summary>Demo/design-time stand-in: usage data is simply unavailable.</summary>
public sealed class NullUsageService : IUsageService
{
    /// <summary>
    /// Asynchronously retrieves the current usage snapshot, returning null when no usage data is available.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the usage snapshot?.</returns>
    public Task<UsageSnapshot?> GetUsageAsync(CancellationToken cancellationToken) =>
        Task.FromResult<UsageSnapshot?>(null);
}
