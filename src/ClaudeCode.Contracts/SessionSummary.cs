using System;

namespace ClaudeCode.Contracts;

/// <summary>One entry from <see cref="IAcpAgentConnection.ListSessionsAsync"/>.</summary>
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

    public string Cwd { get; }

    public string? Title { get; }

    public DateTimeOffset? UpdatedAt { get; }
}
