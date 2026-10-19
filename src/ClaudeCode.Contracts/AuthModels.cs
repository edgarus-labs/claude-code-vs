namespace ClaudeCode.Contracts;

/// <summary>
/// Specifies the available authentication states that describe the current sign-in status of a user session.
/// </summary>
public enum AuthState { Unknown, SignedOut, SigningIn, SignedIn, Error }
