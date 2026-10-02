namespace ClaudeCode.Contracts;

/// <summary>Result of toggling Remote Control (driving the session from claude.ai/code) for a session.</summary>
public sealed class RemoteControlState
{
    /// <summary>
    /// Initializes a new instance of the RemoteControlState class with the specified enabled status and optional session URL.
    /// </summary>
    /// <param name="enabled">The enabled.</param>
    /// <param name="sessionUrl">The session url.</param>
    public RemoteControlState(bool enabled, string? sessionUrl)
    {
        Enabled = enabled;
        SessionUrl = sessionUrl;
    }

    /// <summary>
    /// Gets a value indicating whether enabled.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>Link to the session on claude.ai/code while enabled; null otherwise.</summary>
    public string? SessionUrl { get; }
}
