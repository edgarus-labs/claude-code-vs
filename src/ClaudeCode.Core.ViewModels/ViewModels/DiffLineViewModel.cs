namespace ClaudeCode.Core.ViewModels;

public sealed class DiffLineViewModel
{
    public DiffLineViewModel(DiffLineKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public DiffLineKind Kind { get; }

    public string Text { get; }

    public string Prefix => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };

    public string DisplayText => Prefix + " " + Text;
}
