using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>One entry from the account's usage/rate-limit status (session, weekly, or a
/// model-scoped weekly limit such as Fable).</summary>
public sealed class UsageLimit
{
    public string Kind { get; set; } = "";

    public string Group { get; set; } = "";

    public int Percent { get; set; }

    public string Severity { get; set; } = "normal";

    public DateTimeOffset? ResetsAt { get; set; }

    /// <summary>Display name of the model this limit is scoped to (e.g. "Fable"), or null for an
    /// account-wide limit.</summary>
    public string? ScopeLabel { get; set; }

    public bool IsActive { get; set; }
}

public sealed class UsageSnapshot
{
    public IReadOnlyList<UsageLimit> Limits { get; set; } = Array.Empty<UsageLimit>();

    public DateTimeOffset FetchedAt { get; set; }
}
