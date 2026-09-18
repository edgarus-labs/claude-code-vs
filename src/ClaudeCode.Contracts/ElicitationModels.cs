using System.Collections.Generic;

namespace ClaudeCode.Contracts;

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
    public ElicitationOption(string value, string label, string? description = null)
    {
        Value = value;
        Label = label;
        Description = description;
    }

    public string Value { get; }

    public string Label { get; }

    public string? Description { get; }
}

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

    public string Key { get; }

    public string? Title { get; }

    public string? Description { get; }

    public ElicitationFieldKind Kind { get; }

    public IReadOnlyList<ElicitationOption> Options { get; }
}

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
    public ElicitationAnswer(ElicitationAction action, IReadOnlyDictionary<string, IReadOnlyList<string>> content)
    {
        Action = action;
        Content = content;
    }

    public ElicitationAction Action { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Content { get; }
}
