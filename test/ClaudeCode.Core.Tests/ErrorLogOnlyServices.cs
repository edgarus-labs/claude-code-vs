using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;

namespace ClaudeCode.Core.Tests;

internal sealed class ErrorLogOnlyServices : PlainChatSessionServices, IChatErrorLog
{
    public ErrorLogOnlyServices(StubChatSessionServices inner) : base(inner) { }

    /// <summary>
    /// Gets the collection of logged errors.
    /// </summary>
    public List<(string Message, Exception Exception)> LoggedErrors { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether throw on log.
    /// </summary>
    public bool ThrowOnLog { get; set; }

    public void LogError(string message, Exception exception)
    {
        LoggedErrors.Add((message, exception));
        if (ThrowOnLog)
        {
            throw new InvalidOperationException("log failed");
        }
    }
}
