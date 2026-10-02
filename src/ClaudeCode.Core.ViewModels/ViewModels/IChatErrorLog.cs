using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Host service that records failures.
/// </summary>
public interface IChatErrorLog
{
    /// <summary>Records a failure. Called on the UI thread; returns promptly and never throws.</summary>
    void LogError(string message, Exception exception);
}
