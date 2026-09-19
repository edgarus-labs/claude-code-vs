namespace ClaudeCode.Core.ViewModels;

/// <summary>A single usage/rate-limit row formatted for display (used by the usage popover).</summary>
public sealed class UsageLimitDisplay
{
    public UsageLimitDisplay(string label, int percent, string? resetText, bool isWarning)
    {
        Label = label;
        Percent = percent;
        ResetText = resetText;
        IsWarning = isWarning;
    }

    public string Label { get; }

    public int Percent { get; }

    public string? ResetText { get; }

    public bool IsWarning { get; }
}
