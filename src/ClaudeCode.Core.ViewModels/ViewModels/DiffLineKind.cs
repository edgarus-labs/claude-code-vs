namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Specifies the types of lines in a diff, including context, added, and removed lines.
/// </summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}
