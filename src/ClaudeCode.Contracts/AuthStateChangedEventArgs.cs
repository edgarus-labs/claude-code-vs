namespace ClaudeCode.Contracts;

public sealed class AuthStateChangedEventArgs : System.EventArgs
{
    public AuthStateChangedEventArgs(AuthState state, string? detail = null) { State = state; Detail = detail; }

    /// <summary>
    /// Gets the state.
    /// </summary>
    public AuthState State { get; }

    /// <summary>
    /// Gets the detail.
    /// </summary>
    public string? Detail { get; }
}
