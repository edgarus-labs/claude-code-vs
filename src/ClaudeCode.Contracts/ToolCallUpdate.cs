using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class ToolCallUpdate
{
    public string ToolCallId { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Kind { get; set; }

    public ToolCallStatus Status { get; set; }

    public IReadOnlyList<ToolCallContent> Content { get; set; } = Array.Empty<ToolCallContent>();
}
