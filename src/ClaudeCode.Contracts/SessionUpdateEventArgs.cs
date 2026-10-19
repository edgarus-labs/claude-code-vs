using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Represents an event that occurs when a session is updated, containing the session identifier and the update details.
/// </summary>
public sealed class SessionUpdateEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the SessionUpdateEventArgs class with the specified session identifier and update details.
    /// </summary>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <param name="update">The update.</param>
    public SessionUpdateEventArgs(string sessionId, SessionUpdate update) { SessionId = sessionId; Update = update; }

    /// <summary>
    /// Gets the session id.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the update.
    /// </summary>
    public SessionUpdate Update { get; }
}
