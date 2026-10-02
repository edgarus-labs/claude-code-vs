using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>Reports the account's current usage/rate-limit status (session, weekly, and any
/// model-scoped weekly limits). Returns null rather than throwing when usage data is unavailable
/// (not signed in, offline, or the endpoint failed).</summary>
public interface IUsageService
{
    Task<UsageSnapshot?> GetUsageAsync(CancellationToken cancellationToken);
}
