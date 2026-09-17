using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class VsControlResponse
{
    public string Id { get; set; } = "";

    public string? ResultJson { get; set; }

    public string? Error { get; set; }
}
