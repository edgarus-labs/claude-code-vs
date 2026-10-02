namespace ClaudeCode.Contracts;

public sealed class ToolCallContent
{
    /// <summary>
    /// Gets or sets the text.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Gets or sets the path.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the old text.
    /// </summary>
    public string? OldText { get; set; }

    /// <summary>
    /// Gets or sets the new text.
    /// </summary>
    public string? NewText { get; set; }

    /// <summary>
    /// Gets a value indicating whether is diff.
    /// </summary>
    public bool IsDiff => Path is not null && NewText is not null;
}
