using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>The levels Auto effort may choose.</summary>
public enum EffortLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
}

public static class EffortLevelExtensions
{
    /// <summary>Returns the value the agent uses for this level. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for a value that is not a defined level.</summary>
    public static string ToAgentValue(this EffortLevel level) => level switch
    {
        EffortLevel.Low => "low",
        EffortLevel.Medium => "medium",
        EffortLevel.High => "high",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not an effort level Auto may choose."),
    };
}

/// <summary>Decides, per turn, how much reasoning effort a prompt needs.</summary>
public interface IEffortClassifier
{
    /// <summary>Classifies one prompt. Throws <see cref="System.OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled; any other exception means no verdict could
    /// be obtained.</summary>
    Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken);
}
