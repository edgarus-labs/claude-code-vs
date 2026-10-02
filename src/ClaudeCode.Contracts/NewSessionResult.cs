using System.Collections.Generic;

namespace ClaudeCode.Contracts;

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
