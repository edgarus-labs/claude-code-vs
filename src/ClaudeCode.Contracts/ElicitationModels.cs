using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Specifies the types of fields available for elicitation purposes.
/// </summary>
public enum ElicitationFieldKind
{
    Text,
    SingleSelect,
    MultiSelect,
}

/// <summary>One selectable value within a <see cref="ElicitationField"/> of kind
/// <see cref="ElicitationFieldKind.SingleSelect"/> or <see cref="ElicitationFieldKind.MultiSelect"/>.</summary>
public sealed class ElicitationOption
{
    /// <summary>
    /// Initializes a new instance of the ElicitationOption class with the specified value, label, and optional description.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="label">The label.</param>
    /// <param name="description">The description.</param>
    public ElicitationOption(string value, string label, string? description = null)
    {
        Value = value;
        Label = label;
        Description = description;
    }

    /// <summary>
    /// Gets the value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the label.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    public string? Description { get; }
}

/// <summary>One field of an <see cref="ElicitationRequestEventArgs"/> form, in wire order.</summary>
public sealed class ElicitationField
{
    /// <summary>
    /// Initializes a new instance of the ElicitationField class with the specified key, title, description, kind, and options.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="title">The title.</param>
    /// <param name="description">The description.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="options">The collection of options.</param>
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

/// <summary>
/// Specifies the available user response options for an elicitation action.
/// </summary>
public enum ElicitationAction
{
    Accept,
    Decline,
    Cancel,
}

/// <summary>The user's answer to an <see cref="ElicitationRequestEventArgs"/>. A field key absent
/// from <see cref="Content"/> means the user left that field blank - never send an empty list to
/// mean "answered with nothing".</summary>
public sealed class ElicitationAnswer
{
    /// <summary>
    /// Initializes a new instance of the ElicitationAnswer class with the specified action and content.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="content">The content.</param>
    public ElicitationAnswer(ElicitationAction action, IReadOnlyDictionary<string, IReadOnlyList<string>> content)
    {
        Action = action;
        Content = content;
    }

    /// <summary>
    /// Gets the action.
    /// </summary>
    public ElicitationAction Action { get; }

    /// <summary>
    /// Gets the content.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Content { get; }
}
