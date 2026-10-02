namespace ClaudeCode.VsControl.Mcp;

public sealed class VsControlToolDefinition
{
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
