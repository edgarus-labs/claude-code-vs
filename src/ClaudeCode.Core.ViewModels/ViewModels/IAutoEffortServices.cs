using ClaudeCode.Contracts;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// What a host adds to <see cref="IChatSessionServices"/> to offer Auto effort. Kept apart from that
/// interface so its other implementers are unaffected: a host that does not implement this one simply
/// does not offer Auto. It extends <see cref="IChatErrorLog"/> because a failed judgment is logged, so a
/// host that offers Auto also keeps the panel's error log.
/// </summary>
public interface IAutoEffortServices : IChatErrorLog
{
    /// <summary>The judge behind the Auto effort option, asked once per Auto turn; null when there is
    /// none, in which case Auto is not offered.</summary>
    IEffortClassifier? EffortClassifier { get; }
}
