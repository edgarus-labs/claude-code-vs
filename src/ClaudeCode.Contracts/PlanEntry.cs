namespace ClaudeCode.Contracts;

public sealed class PlanEntry
{
    /// <summary>
    /// Gets or sets the content.
    /// </summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public PlanEntryStatus Status { get; set; }
}
