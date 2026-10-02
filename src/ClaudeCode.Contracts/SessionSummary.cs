using System;

namespace ClaudeCode.Contracts;

/// <summary>One entry from <see cref="IAcpAgentConnection.ListSessionsAsync"/>. Every field is
/// reported by the agent process and is untrusted input.</summary>
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

    /// <summary>The working directory the agent recorded for this session. Agent-chosen and
    /// untrusted; a display hint only.</summary>
    public string Cwd { get; }

    public string? Title { get; }

    public DateTimeOffset? UpdatedAt { get; }
}
