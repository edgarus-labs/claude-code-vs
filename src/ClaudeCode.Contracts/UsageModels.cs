using System;

namespace ClaudeCode.Contracts;

/// <summary>One entry from the account's usage/rate-limit status (session, weekly, or a
/// model-scoped weekly limit such as Fable).</summary>
public sealed class UsageLimit
{
    /// <summary>
    /// Gets or sets the kind.
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>
    /// Gets or sets the group.
    /// </summary>
    public string Group { get; set; } = "";

    /// <summary>
    /// Gets or sets the percent.
    /// </summary>
    public int Percent { get; set; }

    /// <summary>
    /// Gets or sets the severity.
    /// </summary>
    public string Severity { get; set; } = "normal";

    /// <summary>
    /// Gets or sets the resets at.
    /// </summary>
    public DateTimeOffset? ResetsAt { get; set; }

    /// <summary>Display name of the model this limit is scoped to (e.g. "Fable"), or null for an
    /// account-wide limit.</summary>
    public string? ScopeLabel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether is active.
    /// </summary>
    public bool IsActive { get; set; }
}
