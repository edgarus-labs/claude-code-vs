using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class ToolCallUpdate
{
    /// <summary>
    /// Gets or sets the tool call id.
    /// </summary>
    public string ToolCallId { get; set; } = "";

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Gets or sets the kind.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public ToolCallStatus Status { get; set; }

    /// <summary>True when the call runs a subagent (Claude Code's Agent/Task tool). Only reported on
    /// updates that name the tool; an update without it says nothing either way.</summary>
    public bool IsSubagent { get; set; }

    /// <summary>
    /// Gets or sets the collection of content.
    /// </summary>
    public IReadOnlyList<ToolCallContent> Content { get; set; } = Array.Empty<ToolCallContent>();

    /// <summary>The paths the call touches: ACP <c>locations[].path</c> (for claude-agent-acp the file
    /// a Read/Edit/Write works on, a Glob's search folder, or recalled memory files; usually but not
    /// necessarily absolute) plus the files a Glob or Grep found, from claude-agent-acp's
    /// <c>_meta.claudeCode.toolResponse</c>, relative to the session cwd. Empty when the notification
    /// carries none. Agent-supplied and untrusted.</summary>
    public IReadOnlyList<string> Locations { get; set; } = Array.Empty<string>();
}
