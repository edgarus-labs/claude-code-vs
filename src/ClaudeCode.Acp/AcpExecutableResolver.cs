using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCode.Acp
{
    /// <summary>A resolved agent executable plus the arguments needed to put it into ACP mode.</summary>
    public sealed class AcpExecutableSpec
    {
        public AcpExecutableSpec(string fileName, IReadOnlyList<string> arguments)
        {
            FileName = fileName;
            Arguments = arguments;
        }

        /// <summary>Absolute path to the resolved executable.</summary>
        public string FileName { get; }

        /// <summary>Arguments required to run it in ACP mode (empty for `claude-code-acp`, `["--acp"]` for `claude`).</summary>
        public IReadOnlyList<string> Arguments { get; }
    }

    /// <summary>
    /// Locates a usable ACP agent executable on PATH without ever throwing: prefers a dedicated
    /// `claude-code-acp` adapter binary, then falls back to the `claude` CLI itself with `--acp` appended.
    /// Callers surface a friendly "CLI not found" error when this returns null (e.g. so a VS Options page
    /// can prompt the user for an explicit path) instead of this type raising an exception.
    /// </summary>
    public static class AcpExecutableResolver
    {
        private static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';

        private static readonly string[] AcpAdapterCandidateNames = IsWindows
            ? new[] { "claude-code-acp.cmd", "claude-code-acp.exe", "claude-code-acp" }
            : new[] { "claude-code-acp" };

        private static readonly string[] ClaudeCliCandidateNames = IsWindows
            ? new[] { "claude.cmd", "claude.exe", "claude" }
            : new[] { "claude" };

        public static AcpExecutableSpec? TryResolveDefault()
        {
            string? adapterPath = FindOnPath(AcpAdapterCandidateNames);
            if (adapterPath != null)
            {
                return new AcpExecutableSpec(adapterPath, Array.Empty<string>());
            }

            string? claudePath = FindOnPath(ClaudeCliCandidateNames);
            if (claudePath != null)
            {
                return new AcpExecutableSpec(claudePath, new[] { "--acp" });
            }

            return null;
        }

        private static string? FindOnPath(IReadOnlyList<string> candidateFileNames)
        {
            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVariable))
            {
                return null;
            }

            foreach (string directory in pathVariable.Split(Path.PathSeparator))
            {
                if (directory.Length == 0)
                {
                    continue;
                }

                foreach (string candidate in candidateFileNames)
                {
                    string full;
                    try
                    {
                        full = Path.Combine(directory, candidate);
                    }
                    catch (ArgumentException)
                    {
                        continue; // malformed PATH entry.
                    }

                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
            }

            return null;
        }
    }
}
