using ClaudeCode.Contracts;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels.Demo;

/// <summary>Demo/design-time stand-in: usage data is simply unavailable.</summary>
public sealed class NullUsageService : IUsageService
{
    public Task<UsageSnapshot?> GetUsageAsync(CancellationToken cancellationToken) =>
        Task.FromResult<UsageSnapshot?>(null);
}
