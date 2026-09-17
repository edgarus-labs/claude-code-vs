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

    public string SessionId { get; }

    public ToolCallUpdate Call { get; }

    public IReadOnlyList<PermissionOption> Options { get; }

    public TaskCompletionSourceSlot<string> Response { get; } = new TaskCompletionSourceSlot<string>();
}
