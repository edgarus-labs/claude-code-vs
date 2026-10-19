namespace ClaudeCode.Contracts;

/// <summary>A slash command advertised by the agent for the current session.</summary>
public sealed class AvailableCommand
{
    /// <summary>
    /// Initializes a new instance of the AvailableCommand class with the specified name, description, and optional input hint.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="description">The description.</param>
    /// <param name="inputHint">The input hint.</param>
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
