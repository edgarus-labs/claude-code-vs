namespace ClaudeCode.Contracts;

/// <summary>Result of toggling Remote Control (driving the session from claude.ai/code) for a session.</summary>
public sealed class RemoteControlState
{
    public RemoteControlState(bool enabled, string? sessionUrl, string? connectUrl)
    {
        Enabled = enabled;
        SessionUrl = sessionUrl;
        ConnectUrl = connectUrl;
    }

    public bool Enabled { get; }

    /// <summary>Link to the session on claude.ai/code while enabled; null otherwise.</summary>
    public string? SessionUrl { get; }

    public string? ConnectUrl { get; }
}
