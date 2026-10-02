using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>
/// Provides extension methods that convert an EffortLevel value to its corresponding agent representation.
/// </summary>
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
