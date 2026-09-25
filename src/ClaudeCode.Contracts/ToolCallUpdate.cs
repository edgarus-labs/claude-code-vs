using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class ToolCallUpdate
{
    public string ToolCallId { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Kind { get; set; }

    public ToolCallStatus Status { get; set; }

    /// <summary>True when the call runs a subagent (Claude Code's Agent/Task tool). Only reported on
    /// updates that name the tool; an update without it says nothing either way.</summary>
    public bool IsSubagent { get; set; }

    public IReadOnlyList<ToolCallContent> Content { get; set; } = Array.Empty<ToolCallContent>();
}
