using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class PermissionRequestEventArgs : EventArgs
{
    public PermissionRequestEventArgs(string sessionId, ToolCallUpdate call, IReadOnlyList<PermissionOption> options)
    {
        SessionId = sessionId;
        Call = call;
        Options = options;
    }

    /// <summary>
    /// Gets the session id.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the call.
    /// </summary>
    public ToolCallUpdate Call { get; }

    /// <summary>
    /// Gets the collection of options.
    /// </summary>
    public IReadOnlyList<PermissionOption> Options { get; }

    /// <summary>
    /// Gets the response.
    /// </summary>
    public TaskCompletionSourceSlot<string> Response { get; } = new TaskCompletionSourceSlot<string>();
}
