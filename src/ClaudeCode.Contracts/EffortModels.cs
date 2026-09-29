using System;
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

public static class EffortLevelExtensions
{
    /// <summary>The value the agent uses for this level: the one place a level becomes a string, so
    /// reordering or extending <see cref="EffortLevel"/> cannot silently mis-map. Faults for a value
    /// that is not a defined level, as a misbehaving <see cref="IEffortClassifier"/> could return.</summary>
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
    /// be obtained (the caller decides the fallback rather than the classifier guessing).</summary>
    Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken);
}
