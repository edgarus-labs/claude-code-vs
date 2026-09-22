using System.Collections.Generic;

namespace ClaudeCode.Acp;

/// <summary>Builds the argument lists for the adapter's bundled CLI auth commands, so the status
/// probe and the interactive login/logout commands agree on the exact invocation.</summary>
public static class AcpAuthCliArguments
{
    public static List<string> Status(AcpExecutableSpec executable) => Combine(executable, "status", "--json");

    public static List<string> Login(AcpExecutableSpec executable) => Combine(executable, "login", "--claudeai");

    public static List<string> Logout(AcpExecutableSpec executable) => Combine(executable, "logout");

    private static List<string> Combine(AcpExecutableSpec executable, params string[] authArgs)
    {
        var arguments = new List<string>(executable.Arguments) { "--cli", "auth" };
        arguments.AddRange(authArgs);
        return arguments;
    }
}
