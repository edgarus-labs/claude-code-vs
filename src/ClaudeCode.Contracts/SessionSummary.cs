using System;

namespace ClaudeCode.Contracts;

/// <summary>One entry from <see cref="IAcpAgentConnection.ListSessionsAsync"/>. Every field is
/// reported by the agent process and is untrusted input.</summary>
public sealed class SessionSummary
{
    /// <summary>
    /// Initializes a new SessionSummary instance with the specified session identifier, current working directory, optional title, and optional last updated timestamp.
    /// </summary>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <param name="cwd">The cwd.</param>
    /// <param name="title">The title.</param>
    /// <param name="updatedAt">The updated at.</param>
    public SessionSummary(string sessionId, string cwd, string? title, DateTimeOffset? updatedAt)
    {
        SessionId = sessionId;
        Cwd = cwd;
        Title = title;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Gets the session id.
    /// </summary>
    public string SessionId { get; }

    /// <summary>The working directory the agent recorded for this session. Agent-chosen and
    /// untrusted; a display hint only.</summary>
    public string Cwd { get; }

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string? Title { get; }

    /// <summary>
    /// Gets the updated at.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; }
}
