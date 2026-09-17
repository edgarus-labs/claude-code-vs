using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class PermissionOption
{
    public string OptionId { get; set; } = "";

    public string Label { get; set; } = "";

    public PermissionOutcome Outcome { get; set; }
}
