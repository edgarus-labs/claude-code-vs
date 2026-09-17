using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>A selectable value advertised by the agent for one session setting.</summary>
public sealed class SessionConfigValue
{
    public SessionConfigValue(string value, string name, string? description = null)
    {
        Value = value;
        Name = name;
        Description = description;
    }

    public string Value { get; }

    public string Name { get; }

    public string? Description { get; }
}

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

    public string Id { get; }

    public string Name { get; }

    public string? Category { get; }

    public string CurrentValue { get; }

    public IReadOnlyList<SessionConfigValue> Options { get; }
}

public sealed class NewSessionResult
{
    public NewSessionResult(string sessionId, IReadOnlyList<SessionConfigOption> configOptions)
    {
        SessionId = sessionId;
        ConfigOptions = configOptions;
    }

    public string SessionId { get; }

    public IReadOnlyList<SessionConfigOption> ConfigOptions { get; }
}
