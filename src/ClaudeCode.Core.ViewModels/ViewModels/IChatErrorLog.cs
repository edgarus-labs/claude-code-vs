using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// What a host adds to <see cref="IChatSessionServices"/> to keep a record of failures. Kept apart from
/// that interface so its other implementers are unaffected: a host that does not implement this one
/// only shows failures in the status line.
/// </summary>
public interface IChatErrorLog
{
    /// <summary>Records a failure the user sees at most as a one-line status (or not at all, once the
    /// panel is closed), so a bug report has more to go on than that line. Called on the UI thread from
    /// the panel's catch blocks: it must return promptly and must not throw.</summary>
    void LogError(string message, Exception exception);
}
