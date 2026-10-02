namespace ClaudeCode.Core.ViewModels;

/// <summary>A single usage/rate-limit row formatted for display (used by the usage popover).</summary>
public sealed class UsageLimitDisplay
{
    /// <summary>
    /// Initializes a new instance of the UsageLimitDisplay class with the specified label, usage percentage, optional reset text, and warning flag.
    /// </summary>
    /// <param name="label">The label.</param>
    /// <param name="percent">The percent.</param>
    /// <param name="resetText">The reset text.</param>
    /// <param name="isWarning">The is warning.</param>
    public UsageLimitDisplay(string label, int percent, string? resetText, bool isWarning)
    {
        Label = label;
        Percent = percent;
        ResetText = resetText;
        IsWarning = isWarning;
    }

    /// <summary>
    /// Gets the label.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the percent.
    /// </summary>
    public int Percent { get; }

    /// <summary>
    /// Gets the reset text.
    /// </summary>
    public string? ResetText { get; }

    /// <summary>
    /// Gets a value indicating whether is warning.
    /// </summary>
    public bool IsWarning { get; }
}
