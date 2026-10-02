namespace ClaudeCode.Contracts;

/// <summary>A selectable value advertised by the agent for one session setting.</summary>
public sealed class SessionConfigValue
{
    public SessionConfigValue(string value, string name, string? description = null)
    {
        Value = value;
        Name = name;
        Description = description;
    }

    /// <summary>
    /// Gets the value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    public string? Description { get; }
}
