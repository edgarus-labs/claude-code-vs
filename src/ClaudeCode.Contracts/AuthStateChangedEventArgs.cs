namespace ClaudeCode.Contracts;

public sealed class AuthStateChangedEventArgs : System.EventArgs
{
    public AuthStateChangedEventArgs(AuthState state, string? detail = null) { State = state; Detail = detail; }

    public AuthState State { get; }

    public string? Detail { get; }
}
