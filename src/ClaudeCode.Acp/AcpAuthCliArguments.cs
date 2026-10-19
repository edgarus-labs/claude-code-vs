using System;
using System.Collections.Generic;

namespace ClaudeCode.Acp;

/// <summary>Builds the argument lists for the adapter's bundled CLI auth commands, so the status
/// probe and the interactive login/logout commands agree on the exact invocation.</summary>
public static class AcpAuthCliArguments
{
    /// <summary>
    /// Generates a command to retrieve the status of the specified executable in JSON format.
    /// </summary>
    /// <param name="executable">The executable.</param>
    /// <returns>A collection of iread only list items.</returns>
    public static IReadOnlyList<string> Status(AcpExecutableSpec executable) => Combine(executable, "status", "--json");

    /// <summary>
    /// Generates the login command for the specified executable with the &apos;--claudeai&apos; flag.
    /// </summary>
    /// <param name="executable">The executable.</param>
    /// <returns>A collection of iread only list items.</returns>
    public static IReadOnlyList<string> Login(AcpExecutableSpec executable) => Combine(executable, "login", "--claudeai");

    /// <summary>
    /// Generates a logout command for the specified executable specification.
    /// </summary>
    /// <param name="executable">The executable.</param>
    /// <returns>A collection of iread only list items.</returns>
    public static IReadOnlyList<string> Logout(AcpExecutableSpec executable) => Combine(executable, "logout");

    /// <summary>
    /// Combines the executable arguments with the specified authentication arguments and returns the combined list.
    /// </summary>
    /// <param name="executable">The executable.</param>
    /// <param name="authArgs">The collection of auth args.</param>
    /// <returns>A collection of list items.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an error occurs during execution.</exception>
    private static List<string> Combine(AcpExecutableSpec executable, params string[] authArgs)
    {
        if (executable is null) throw new ArgumentNullException(nameof(executable));
        var arguments = new List<string>(executable.Arguments) { "--cli", "auth" };
        arguments.AddRange(authArgs);
        return arguments;
    }
}
