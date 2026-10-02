using ClaudeCode.Contracts;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels.Demo;

/// <summary>Demo/design-time usage service that reports no usage data.</summary>
public sealed class NullUsageService : IUsageService
{
    public Task<UsageSnapshot?> GetUsageAsync(CancellationToken cancellationToken) =>
        Task.FromResult<UsageSnapshot?>(null);
}
