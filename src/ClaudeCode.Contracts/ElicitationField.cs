using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>One field of an <see cref="ElicitationRequestEventArgs"/> form, in wire order.</summary>
public sealed class ElicitationField
{
    public ElicitationField(string key, string? title, string? description, ElicitationFieldKind kind, IReadOnlyList<ElicitationOption> options)
    {
        Key = key;
        Title = title;
        Description = description;
        Kind = kind;
        Options = options;
    }

    /// <summary>
    /// Gets the key.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string? Title { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets the kind.
    /// </summary>
    public ElicitationFieldKind Kind { get; }

    /// <summary>
    /// Gets the collection of options.
    /// </summary>
    public IReadOnlyList<ElicitationOption> Options { get; }
}
