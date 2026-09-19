using System;

namespace ClaudeCode.Contracts;

/// <summary>One entry from <see cref="IAcpAgentConnection.ListSessionsAsync"/>. Every field is
/// reported by the agent process and is therefore untrusted input, not client state.</summary>
public sealed class SessionSummary
{
    public SessionSummary(string sessionId, string cwd, string? title, DateTimeOffset? updatedAt)
    {
        SessionId = sessionId;
        Cwd = cwd;
        Title = title;
        UpdatedAt = updatedAt;
    }

    public string SessionId { get; }

    /// <summary>The working directory the agent recorded for this session. Untrusted: the agent
    /// chooses this string, so it MUST NOT be passed to
    /// <see cref="IAcpAgentConnection.LoadSessionAsync"/> (which would let the agent pick the root a
    /// host sandboxes itself to) or to any filesystem/VS API without being resolved inside the
    /// client's own workspace boundary first. Treat it as a display hint.</summary>
    public string Cwd { get; }

    public string? Title { get; }

    public DateTimeOffset? UpdatedAt { get; }
}
