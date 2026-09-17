using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class VsControlRequest
{
    public string Id { get; set; } = "";

    public string Method { get; set; } = "";

    public string ParamsJson { get; set; } = "{}";
}
