using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class UsageSnapshot
{
    /// <summary>
    /// Gets or sets the collection of limits.
    /// </summary>
    public IReadOnlyList<UsageLimit> Limits { get; set; } = Array.Empty<UsageLimit>();

    /// <summary>
    /// Gets or sets the fetched at.
    /// </summary>
    public DateTimeOffset FetchedAt { get; set; }
}
