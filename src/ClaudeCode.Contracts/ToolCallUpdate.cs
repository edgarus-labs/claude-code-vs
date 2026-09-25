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

    /// <summary>The paths the call touches (ACP <c>locations[].path</c>): for claude-agent-acp the file
    /// a Read/Edit/Write works on, a Glob's search folder, recalled memory files. Forwarded from the
    /// tool input unchanged, so usually but not necessarily absolute. Empty when the notification
    /// carries none. Agent-supplied, so untrusted: validate before touching the filesystem.</summary>
    public IReadOnlyList<string> Locations { get; set; } = Array.Empty<string>();
}
