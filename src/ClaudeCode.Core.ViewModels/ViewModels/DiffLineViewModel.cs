namespace ClaudeCode.Core.ViewModels
{
    public enum DiffLineKind
    {
        Context,
        Added,
        Removed,
    }

    /// <summary>One rendered line of a unified diff view for a tool-call's file edit.</summary>
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
}
