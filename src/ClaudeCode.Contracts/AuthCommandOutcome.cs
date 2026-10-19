using System;

namespace ClaudeCode.Contracts;

/// <summary>Result of a one-shot native auth command (interactive login or logout) launched by the
/// extension. <see cref="Message"/> is always non-empty and safe to show the user; it is
/// extension-authored text only, never CLI output, tokens or credentials.</summary>
public sealed class AuthCommandOutcome
{
    /// <summary>
    /// Initializes a new instance of the AuthCommandOutcome class with the specified success state and a required, non-empty outcome message.
    /// </summary>
    /// <param name="succeeded">The succeeded.</param>
    /// <param name="message">The message.</param>
    /// <exception cref="ArgumentException">Thrown when an error occurs during execution.</exception>
    /// <exception cref="ArgumentNullException">Thrown when an error occurs during execution.</exception>
    public AuthCommandOutcome(bool succeeded, string message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("An outcome message is required.", nameof(message));
        Succeeded = succeeded;
        Message = message;
    }

    /// <summary>
    /// Gets a value indicating whether succeeded.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the message.
    /// </summary>
    public string Message { get; }
}
