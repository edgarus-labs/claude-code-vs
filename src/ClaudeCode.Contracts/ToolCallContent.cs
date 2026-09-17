using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class ToolCallContent
{
    public string? Text { get; set; }

    public string? Path { get; set; }

    public string? OldText { get; set; }

    public string? NewText { get; set; }

    public bool IsDiff => Path is not null && NewText is not null;
}
