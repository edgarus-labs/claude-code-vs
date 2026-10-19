using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Represents a plan entry containing content and status information.
/// </summary>
public sealed class PlanEntry
{
    /// <summary>
    /// Gets or sets the content.
    /// </summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public PlanEntryStatus Status { get; set; }
}
