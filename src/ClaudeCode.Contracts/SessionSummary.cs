using System;

namespace ClaudeCode.Contracts;

/// <summary>One entry from <see cref="IAcpAgentConnection.ListSessionsAsync"/>. Every field is
/// reported by the agent process and is therefore untrusted input, not client state.</summary>
public sealed class SessionSummary
{
    /// <summary>
    /// Initializes a new instance of the SessionSummary class with the specified session identifier, current working directory, optional title, and optional last updated timestamp.
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

    /// <summary>The working directory the agent recorded for this session. Untrusted: the agent
    /// chooses this string, so it MUST NOT be passed to
    /// <see cref="IAcpAgentConnection.LoadSessionAsync"/> (which would let the agent pick the root a
    /// host sandboxes itself to) or to any filesystem/VS API without being resolved inside the
    /// client's own workspace boundary first. Treat it as a display hint.</summary>
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
