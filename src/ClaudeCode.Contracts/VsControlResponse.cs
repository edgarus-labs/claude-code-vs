namespace ClaudeCode.Contracts;

public sealed class VsControlResponse
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Gets or sets the result json.
    /// </summary>
    public string? ResultJson { get; set; }

    /// <summary>
    /// Gets or sets the error.
    /// </summary>
    public string? Error { get; set; }
}
