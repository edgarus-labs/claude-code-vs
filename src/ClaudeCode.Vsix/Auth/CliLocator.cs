using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCode.Vsix.Auth
{
    /// <summary>
    /// Resolves the executable used for the Claude Code CLI's own OAuth flow (`claude setup-token`).
    /// This is deliberately separate from <c>ClaudeCode.Acp.AcpExecutableResolver</c>, which resolves
    /// whatever binary talks the Agent Client Protocol (which may be a dedicated `claude-code-acp`
    /// adapter that does not implement `setup-token`) - the auth flow specifically needs the base
    /// `claude` CLI.
    /// </summary>
    internal static class CliLocator
    {
        private static readonly string[] WindowsExecutableExtensions = { ".exe", ".cmd", ".bat", "" };

        /// <summary>
        /// Resolves the `claude` executable to run `setup-token` against. Prefers the VS-configured
        /// override only when it plausibly points at the base CLI (file name is exactly "claude"),
        /// since the override is primarily meant for the ACP adapter executable, which is not
        /// guaranteed to support `setup-token`. Falls back to a PATH search, then to the bare command
        /// name so the OS can still attempt to resolve it at process-start time.
        /// </summary>
        public static string ResolveClaudeCliForAuth(string? cliExecutablePathOverride)
        {
            if (!string.IsNullOrWhiteSpace(cliExecutablePathOverride))
            {
                var overrideName = Path.GetFileNameWithoutExtension(cliExecutablePathOverride);
                if (string.Equals(overrideName, "claude", StringComparison.OrdinalIgnoreCase))
                {
                    return cliExecutablePathOverride!;
                }
            }

            return FindOnPath("claude") ?? "claude";
        }

        /// <summary>Searches the PATH environment variable for an executable with the given base name.</summary>
        private static string? FindOnPath(string exeNameWithoutExtension)
        {
            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in pathVariable.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                foreach (var extension in WindowsExecutableExtensions)
                {
                    string candidate;
                    try
                    {
                        candidate = Path.Combine(directory, exeNameWithoutExtension + extension);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
    }
}
