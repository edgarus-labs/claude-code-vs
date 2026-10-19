namespace ClaudeCode.Contracts;

/// <summary>
/// Represents a Visual Studio control request containing the identifier, method name, and parameters in JSON format.
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
