using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>Authoritative select configuration; grouped wire options are flattened in display order.</summary>
public sealed class SessionConfigOption
{
    public SessionConfigOption(string id, string name, string? category, string currentValue, IReadOnlyList<SessionConfigValue> options)
    {
        Id = id;
        Name = name;
        Category = category;
        CurrentValue = currentValue;
        Options = options;
    }

    /// <summary>
    /// Gets the id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the category.
    /// </summary>
    public string? Category { get; }

    /// <summary>
    /// Gets the current value.
    /// </summary>
    public string CurrentValue { get; }

    /// <summary>
    /// Gets the collection of options.
    /// </summary>
    public IReadOnlyList<SessionConfigValue> Options { get; }
}
