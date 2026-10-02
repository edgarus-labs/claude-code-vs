namespace ClaudeCode.Contracts;

public sealed class PermissionOption
{
    /// <summary>
    /// Gets or sets the option id.
    /// </summary>
    public string OptionId { get; set; } = "";

    /// <summary>
    /// Gets or sets the label.
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// Gets or sets the outcome.
    /// </summary>
    public PermissionOutcome Outcome { get; set; }
}
