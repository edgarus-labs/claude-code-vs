using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Represents an event argument containing details for an elicitation request, including session information, message content, field data, and a response completion source.
/// </summary>
public sealed class ElicitationRequestEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the ElicitationRequestEventArgs class with the specified session identifier, message, and collection of elicitation fields.
    /// </summary>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <param name="message">The message.</param>
    /// <param name="fields">The collection of fields.</param>
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
