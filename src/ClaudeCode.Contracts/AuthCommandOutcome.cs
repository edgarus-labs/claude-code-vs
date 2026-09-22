using System;

namespace ClaudeCode.Contracts;

/// <summary>Result of a one-shot native auth command (interactive login or logout) launched by the
/// extension. <see cref="Message"/> is always non-empty and safe to show the user; it is
/// extension-authored text only, never CLI output, tokens or credentials.</summary>
public sealed class AuthCommandOutcome
{
    public AuthCommandOutcome(bool succeeded, string message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("An outcome message is required.", nameof(message));
        Succeeded = succeeded;
        Message = message;
    }

    public bool Succeeded { get; }

    public string Message { get; }
}
