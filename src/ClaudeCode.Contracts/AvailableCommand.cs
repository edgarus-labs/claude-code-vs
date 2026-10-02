namespace ClaudeCode.Contracts;

/// <summary>A slash command advertised by the agent for the current session.</summary>
public sealed class AvailableCommand
{
    public AvailableCommand(string name, string description, string? inputHint = null)
    {
        Name = name;
        Description = description;
        InputHint = inputHint;
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
    /// Gets the input hint.
    /// </summary>
    public string? InputHint { get; }
}
