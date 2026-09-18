using System;

namespace ClaudeCode.Contracts;

/// <summary>
/// Constant-time comparison for the VsControl named-pipe handshake token (see
/// <see cref="WorkspacePathGuard"/> for the sibling path-containment contract). A naive
/// <c>string.Equals</c> short-circuits on the first mismatched character, letting another local
/// process recover the expected token byte-by-byte via a timing side channel; this walks the full
/// length of both inputs regardless of where they first differ, with no early return.
/// </summary>
public static class ConstantTimeTokenComparer
{
    public static bool Equals(string? received, string expected)
    {
        if (received is null)
        {
            return false;
        }

        int diff = received.Length ^ expected.Length;
        int maxLength = Math.Max(received.Length, expected.Length);

        for (int i = 0; i < maxLength; i++)
        {
            char a = i < received.Length ? received[i] : '\0';
            char b = i < expected.Length ? expected[i] : '\0';
            diff |= a ^ b;
        }

        return diff == 0;
    }
}
