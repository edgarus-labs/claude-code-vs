using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class PlanEntry
{
    public string Content { get; set; } = "";

    public PlanEntryStatus Status { get; set; }
}
