namespace ClaudeCode.Contracts;

/// <summary>
/// Represents a request to a Visual Studio control, encapsulating an identifier, the method name to invoke, and JSON‑encoded parameters.
/// </summary>
public sealed class VsControlRequest
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Gets or sets the method.
    /// </summary>
    public string Method { get; set; } = "";

    /// <summary>
    /// Gets or sets the params json.
    /// </summary>
    public string ParamsJson { get; set; } = "{}";
}
