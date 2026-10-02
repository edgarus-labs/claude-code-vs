using ClaudeCode.Contracts;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Host services that enable the Auto effort option.
/// </summary>
public interface IAutoEffortServices : IChatErrorLog
{
    /// <summary>The judge behind the Auto effort option, asked once per Auto turn; null when there is
    /// none, in which case Auto is not offered.</summary>
    IEffortClassifier? EffortClassifier { get; }
}
