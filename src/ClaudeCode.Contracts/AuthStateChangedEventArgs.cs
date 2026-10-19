namespace ClaudeCode.Contracts;

/// <summary>
/// Represents authentication state change event arguments containing the new authentication state and optional details.
/// </summary>
public sealed class AuthStateChangedEventArgs : System.EventArgs
{
    /// <summary>
    /// Initializes a new instance of the AuthStateChangedEventArgs class with the specified authentication state and an optional detail message.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="detail">The detail.</param>
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
