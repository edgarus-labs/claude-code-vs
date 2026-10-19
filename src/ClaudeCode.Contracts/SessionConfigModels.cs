using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>A selectable value advertised by the agent for one session setting.</summary>
public sealed class SessionConfigValue
{
    /// <summary>
    /// Initializes a new instance of the SessionConfigValue class with the specified value, name, and optional description.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="name">The name.</param>
    /// <param name="description">The description.</param>
    public SessionConfigValue(string value, string name, string? description = null)
    {
        Value = value;
        Name = name;
        Description = description;
    }

    /// <summary>
    /// Gets the value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    public string? Description { get; }
}

/// <summary>Authoritative select configuration; grouped wire options are flattened in display order.</summary>
public sealed class SessionConfigOption
{
    /// <summary>
    /// Initializes a new instance of the SessionConfigOption class with the specified identifier, name, category, current value, and available options.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="name">The name.</param>
    /// <param name="category">The category.</param>
    /// <param name="currentValue">The current value.</param>
    /// <param name="options">The collection of options.</param>
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

/// <summary>
/// Represents the result of creating a new session, containing the session identifier and configuration options.
/// </summary>
public sealed class NewSessionResult
{
    /// <summary>
    /// Initializes a new instance of the NewSessionResult class with the specified session identifier and configuration options.
    /// </summary>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <param name="configOptions">The collection of config options.</param>
    public NewSessionResult(string sessionId, IReadOnlyList<SessionConfigOption> configOptions)
    {
        SessionId = sessionId;
        ConfigOptions = configOptions;
    }

    /// <summary>
    /// Gets the session id.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the collection of config options.
    /// </summary>
    public IReadOnlyList<SessionConfigOption> ConfigOptions { get; }
}
