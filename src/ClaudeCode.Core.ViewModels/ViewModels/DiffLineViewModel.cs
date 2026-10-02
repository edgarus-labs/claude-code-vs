namespace ClaudeCode.Core.ViewModels;

public sealed class DiffLineViewModel
{
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
