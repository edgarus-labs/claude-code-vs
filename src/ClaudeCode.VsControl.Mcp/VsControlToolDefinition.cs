namespace ClaudeCode.VsControl.Mcp;

public sealed class VsControlToolDefinition
{
    public VsControlToolDefinition(string name, string description, string inputSchemaJson)
    {
        Name = name;
        Description = description;
        InputSchemaJson = inputSchemaJson;
    }

    public string Name { get; }

    public string Description { get; }

    public string InputSchemaJson { get; }
}
