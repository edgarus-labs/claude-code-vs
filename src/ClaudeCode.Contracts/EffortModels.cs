using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>The levels Auto effort may choose. Higher agent levels stay an explicit user choice.</summary>
public enum EffortLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>Decides, per turn, how much reasoning effort a prompt needs.</summary>
public interface IEffortClassifier
{
    /// <summary>Classifies one prompt. Throws <see cref="System.OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled; any other exception means no verdict could
    /// be obtained (the caller decides the fallback rather than the classifier guessing).</summary>
    Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken);
}
