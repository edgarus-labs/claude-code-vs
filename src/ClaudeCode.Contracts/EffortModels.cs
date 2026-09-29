using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>The levels Auto effort may choose. Higher agent levels stay an explicit user choice.</summary>
public enum EffortLevel
{
    Low,
    Medium,
    High,
}

/// <summary>Decides, per turn, how much reasoning effort a prompt needs.</summary>
public interface IEffortClassifier
{
    /// <summary>Classifies one prompt. Faults (rather than guessing) when no verdict can be
    /// obtained, so the caller decides the fallback.</summary>
    Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken);
}
