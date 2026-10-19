using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Represents the event arguments for a permission request, containing the session identifier, tool call update, permission options, and a task completion source for the response.
/// </summary>
public sealed class PermissionRequestEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the PermissionRequestEventArgs class with the specified session identifier, tool call update, and permission options.
    /// </summary>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <param name="call">The call.</param>
    /// <param name="options">The collection of options.</param>
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
