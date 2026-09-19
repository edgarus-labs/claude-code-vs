using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>Reports the account's current usage/rate-limit status (session, weekly, and any
/// model-scoped weekly limits). Returns null when usage data is unavailable (not signed in,
/// offline, or the endpoint failed) rather than throwing, so callers can treat it as "unknown"
/// and simply hide usage UI.</summary>
public interface IUsageService
{
    Task<UsageSnapshot?> GetUsageAsync(CancellationToken cancellationToken);
}
