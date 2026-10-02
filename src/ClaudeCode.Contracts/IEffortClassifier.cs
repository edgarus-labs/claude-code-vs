using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>Decides, per turn, how much reasoning effort a prompt needs.</summary>
public interface IEffortClassifier
{
    /// <summary>Classifies one prompt. Throws <see cref="System.OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled; any other exception means no verdict could
    /// be obtained.</summary>
    Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken);
}
