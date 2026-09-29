using ClaudeCode.Contracts;
using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// What a host adds to <see cref="IChatSessionServices"/> to offer Auto effort. Kept apart from that
/// interface so its other implementers are unaffected: a host that does not implement this one simply
/// does not offer Auto.
/// </summary>
public interface IAutoEffortServices
{
    /// <summary>The judge behind the Auto effort option, asked once per Auto turn; null when there is
    /// none, in which case Auto is not offered.</summary>
    IEffortClassifier? EffortClassifier { get; }

    /// <summary>Records a failure that is otherwise only shown to the user as a one-line status, so a
    /// bug report has more to go on than that line. Must not throw.</summary>
    void LogError(string message, Exception exception);
}
