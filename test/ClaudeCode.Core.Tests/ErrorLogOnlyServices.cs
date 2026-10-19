using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;

namespace ClaudeCode.Core.Tests;

/// <summary>A host that keeps a log but does not offer Auto effort. <see cref="ThrowOnLog"/> makes the
/// log break its "must not throw" contract.</summary>
internal sealed class ErrorLogOnlyServices : PlainChatSessionServices, IChatErrorLog
{
    /// <summary>
    /// Initializes a new instance of the ErrorLogOnlyServices class with the specified inner chat session services, delegating all other behavior to the base implementation.
    /// </summary>
    /// <param name="inner">The inner.</param>
    public ErrorLogOnlyServices(StubChatSessionServices inner) : base(inner) { }

    /// <summary>
    /// Gets the collection of logged errors.
    /// </summary>
    public List<(string Message, Exception Exception)> LoggedErrors { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether throw on log.
    /// </summary>
    public bool ThrowOnLog { get; set; }

    /// <summary>
    /// Records the specified error message and exception in the collection of logged errors, and throws an InvalidOperationException if logging is configured to fail.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="exception">The exception.</param>
    /// <exception cref="InvalidOperationException">Thrown when an error occurs during execution.</exception>
    public void LogError(string message, Exception exception)
    {
        LoggedErrors.Add((message, exception));
        if (ThrowOnLog)
        {
            throw new InvalidOperationException("log failed");
        }
    }
}
