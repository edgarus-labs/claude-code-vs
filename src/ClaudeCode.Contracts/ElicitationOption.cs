using System.Collections.Generic;

namespace ClaudeCode.Contracts;

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
