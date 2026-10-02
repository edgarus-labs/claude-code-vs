using System;

namespace ClaudeCode.Contracts;

public sealed class SessionUpdateEventArgs : EventArgs
{
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
