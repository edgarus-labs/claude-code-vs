namespace ClaudeCode.VsControl.Mcp;

/// <summary>
/// Represents a control tool definition with name, description, and input schema details.
/// </summary>
public sealed class VsControlToolDefinition
{
    /// <summary>
    /// Initializes a new instance of the VsControlToolDefinition class with the specified tool name, description, and JSON input schema.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="description">The description.</param>
    /// <param name="inputSchemaJson">The input schema json.</param>
    public VsControlToolDefinition(string name, string description, string inputSchemaJson)
    {
        Name = name;
        Description = description;
        InputSchemaJson = inputSchemaJson;
    }

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the input schema json.
    /// </summary>
    public string InputSchemaJson { get; }
}
