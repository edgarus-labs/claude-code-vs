using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class SessionUpdateEventArgs : EventArgs
{
    public SessionUpdateEventArgs(string sessionId, SessionUpdate update) { SessionId = sessionId; Update = update; }

    public string SessionId { get; }

    public SessionUpdate Update { get; }
}
