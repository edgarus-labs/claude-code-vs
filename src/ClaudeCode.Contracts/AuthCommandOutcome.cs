using System;

namespace ClaudeCode.Contracts;

/// <summary>Result of a one-shot native auth command (interactive login or logout) launched by the
/// extension. <see cref="Message"/> is always set and safe to show the user; it never carries
/// tokens or credentials, only the CLI's exit status and the extension's own diagnostic text.</summary>
public sealed class AuthCommandOutcome
{
    public AuthCommandOutcome(bool succeeded, string message)
    {
        Succeeded = succeeded;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public bool Succeeded { get; }

    public string Message { get; }
}
