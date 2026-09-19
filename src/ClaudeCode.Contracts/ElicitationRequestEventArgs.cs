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

    public string SessionId { get; }

    public string Message { get; }

    public IReadOnlyList<ElicitationField> Fields { get; }

    public TaskCompletionSourceSlot<ElicitationAnswer> Response { get; } = new TaskCompletionSourceSlot<ElicitationAnswer>();
}
