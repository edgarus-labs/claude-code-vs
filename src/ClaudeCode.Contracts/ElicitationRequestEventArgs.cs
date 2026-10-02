using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class ElicitationRequestEventArgs : EventArgs
{
    public ElicitationRequestEventArgs(string sessionId, string message, IReadOnlyList<ElicitationField> fields)
    {
        SessionId = sessionId;
        Message = message;
        Fields = fields;
    }

    /// <summary>
    /// Gets the session id.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the collection of fields.
    /// </summary>
    public IReadOnlyList<ElicitationField> Fields { get; }

    /// <summary>
    /// Gets the response.
    /// </summary>
    public TaskCompletionSourceSlot<ElicitationAnswer> Response { get; } = new TaskCompletionSourceSlot<ElicitationAnswer>();
}
