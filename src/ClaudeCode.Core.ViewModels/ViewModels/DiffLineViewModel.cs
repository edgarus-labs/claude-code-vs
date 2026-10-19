namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Represents a view model for displaying a single line of a diff with its kind, text, prefix, and display text.
/// </summary>
public sealed class DiffLineViewModel
{
    /// <summary>
    /// Initializes a new instance of the DiffLineViewModel class with the specified line kind and text.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="text">The text.</param>
    public DiffLineViewModel(DiffLineKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    /// <summary>
    /// Gets the kind.
    /// </summary>
    public DiffLineKind Kind { get; }

    /// <summary>
    /// Gets the text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the prefix.
    /// </summary>
    public string Prefix => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };

    /// <summary>
    /// Gets the display text.
    /// </summary>
    public string DisplayText => Prefix + " " + Text;
}
