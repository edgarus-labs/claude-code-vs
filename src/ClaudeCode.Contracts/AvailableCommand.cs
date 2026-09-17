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

    public string Name { get; }

    public string Description { get; }

    public string? InputHint { get; }
}
