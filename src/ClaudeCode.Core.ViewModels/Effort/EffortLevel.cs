using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Effort;

/// <summary>The only levels Auto effort may choose. Higher ACP levels stay an explicit user choice.</summary>
public enum EffortLevel
{
    Low,
    Medium,
    High,
}

/// <summary>Decides, locally and per turn, how much reasoning effort a prompt needs.</summary>
public interface IEffortClassifier
{
    /// <summary>Classifies one prompt. Loads its model on first use; faults (rather than guessing)
    /// when the model cannot be loaded or run, so the caller decides the fallback.</summary>
    Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken);
}
