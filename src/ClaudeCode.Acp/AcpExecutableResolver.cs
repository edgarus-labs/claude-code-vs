using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCode.Acp;

/// <summary>
/// Provides static helpers for locating ACP adapter executables by resolving fully qualified paths, searching default and PATH-based locations, and locating Node.js and npm tooling.
/// </summary>
public static class AcpExecutableResolver
{
    private static readonly bool _isWindows = Path.DirectorySeparatorChar == '\\';

    private static readonly string[] _acpAdapterCandidateNames = _isWindows
        ? ["claude-agent-acp.exe", "claude-agent-acp.cmd"]
        : ["claude-agent-acp"];

    private static readonly string _nodeExecutableName = _isWindows ? "node.exe" : "node";

    private static readonly string[] _npmCandidateNames = _isWindows
        ? ["npm.cmd", "npm.exe", "npm"]
        : ["npm"];

    /// <summary>
    /// Determines whether the specified path is fully qualified, returning true for UNC paths and drive-letter paths with a separator on Windows, or for paths beginning with a slash on other platforms.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>true if the condition is met; otherwise, false.</returns>
    public static bool IsFullyQualifiedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!_isWindows)
        {
            return path![0] == '/';
        }

        // Rooted paths such as C:tools and \tools still depend on the current directory or drive.
        return (path!.Length >= 2 && IsDirectorySeparator(path[0]) && IsDirectorySeparator(path[1]))
            || (path.Length >= 3 && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))
                && path[1] == ':' && IsDirectorySeparator(path[2]));
    }

    /// <summary>
    /// Attempts to resolve the default ACP executable specification by searching the default search path, returning null if no matching executable is found.
    /// </summary>
    /// <returns>The acp executable spec? result.</returns>
    public static AcpExecutableSpec? TryResolveDefault() => TryResolveDefault(GetDefaultSearchPath());

    /// <summary>
    /// Builds the default executable search path by combining the process, user, and machine PATH environment variables and the existing npm global directory on Windows, or returning the process PATH on other platforms.
    /// </summary>
    /// <returns>The string result.</returns>
    public static string GetDefaultSearchPath()
    {
        string? processPath = Environment.GetEnvironmentVariable("PATH");
        if (!_isWindows)
        {
            return processPath ?? string.Empty;
        }

        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            paths.Add(processPath!);
        }

        try
        {
            string? userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(userPath))
            {
                paths.Add(userPath!);
            }

            string? machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(machinePath))
            {
                paths.Add(machinePath!);
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                string npmDir = Path.Combine(appData, "npm");
                if (Directory.Exists(npmDir))
                {
                    paths.Add(npmDir);
                }
            }
        }
        catch
        {
            // Best-effort registry read; fall back to process PATH.
        }

        return string.Join(Path.PathSeparator.ToString(), paths);
    }

    /// <summary>
    /// Attempts to resolve the default ACP executable specification by searching through candidate directories and names.
    /// </summary>
    /// <param name="searchPath">The search path.</param>
    /// <returns>The acp executable spec? result.</returns>
    public static AcpExecutableSpec? TryResolveDefault(string? searchPath)
    {
        foreach (string directory in GetSearchDirectories(searchPath))
        {
            foreach (string candidate in _acpAdapterCandidateNames)
            {
                var resolved = TryResolve(Path.Combine(directory, candidate), searchPath);
                if (resolved is not null)
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to resolve an ACP executable specification from the specified path using the system PATH environment variable.
    /// </summary>
    /// <param name="executablePath">The executable path.</param>
    /// <returns>The acp executable spec? result.</returns>
    public static AcpExecutableSpec? TryResolve(string executablePath) =>
        TryResolve(executablePath, Environment.GetEnvironmentVariable("PATH"));

    /// <summary>
    /// Attempts to resolve an ACP executable specification from the given path, validating file existence and path format, and returns null if the path is invalid or the file does not exist.
    /// </summary>
    /// <param name="executablePath">The executable path.</param>
    /// <param name="searchPath">The search path.</param>
    /// <returns>The acp executable spec? result.</returns>
    public static AcpExecutableSpec? TryResolve(string executablePath, string? searchPath)
    {
        if (!IsFullyQualifiedPath(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(executablePath);
        string extension = Path.GetExtension(fullPath);
        string directory = Path.GetDirectoryName(fullPath)!;
        string? scriptPath = null;
        bool preferAdjacentNode = false;
        if (_isWindows && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            // Never execute a command shell or interpolate arguments into an npm shim.
            if (!Path.GetFileNameWithoutExtension(fullPath).Equals("claude-agent-acp", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            scriptPath = Path.Combine(directory, "node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js");
            if (!File.Exists(scriptPath) && Path.GetFileName(directory).Equals(".bin", StringComparison.OrdinalIgnoreCase))
            {
                scriptPath = Path.Combine(directory, "..", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js");
            }

            if (!File.Exists(scriptPath))
            {
                return null;
            }

            // Only global shim directories may supply an adjacent runtime. Package directories
            // remain untrusted regardless of which package layout supplied the script.
            preferAdjacentNode = !IsPackageDirectory(directory);
        }
        else if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            if (!fullPath.Replace('\\', '/').EndsWith("/@agentclientprotocol/claude-agent-acp/dist/index.js", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Always PATH-first: the entry point's directory is package content (node_modules), so a
            // "node.exe" found there is never a trusted adjacent runtime.
            scriptPath = fullPath;
        }
        else
        {
            string adapterName = _isWindows ? "claude-agent-acp.exe" : "claude-agent-acp";
            if (!Path.GetFileName(fullPath).Equals(adapterName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new AcpExecutableSpec(fullPath, []);
        }

        string? nodePath = null;
        if (preferAdjacentNode)
        {
            string adjacentNode = Path.Combine(directory, _nodeExecutableName);
            if (File.Exists(adjacentNode))
            {
                nodePath = adjacentNode;
            }
        }

        if (nodePath is null)
        {
            nodePath = FindOnPath(_nodeExecutableName, searchPath);
        }

        return nodePath is null ? null : new AcpExecutableSpec(nodePath, [Path.GetFullPath(scriptPath)]);
    }

    /// <summary>Finds a Node runtime on <paramref name="searchPath"/> using the same trusted filter
    /// the adapter launch uses: only fully qualified entries (never the current drive or directory)
    /// and never a package directory, whose content a workspace can plant. Returns null when no
    /// trusted entry holds one.</summary>
    public static string? FindNodeOnPath(string? searchPath) => FindOnPath(_nodeExecutableName, searchPath);

    /// <summary>
    /// Searches for the npm executable on the specified path by checking common candidate names.
    /// </summary>
    /// <param name="searchPath">The search path.</param>
    /// <returns>The string? result.</returns>
    public static string? FindNpmOnPath(string? searchPath)
    {
        foreach (string candidate in _npmCandidateNames)
        {
            string? found = FindOnPath(candidate, searchPath);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Determines whether the specified character is a directory separator character.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>true if the condition is met; otherwise, false.</returns>
    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    /// <summary>
    /// Determines whether the specified directory path contains either a &apos;node_modules&apos; or &apos;.bin&apos; segment.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <returns>true if the condition is met; otherwise, false.</returns>
    private static bool IsPackageDirectory(string directory)
    {
        int segmentStart = 0;
        for (int index = 0; index <= directory.Length; index++)
        {
            if (index != directory.Length && !IsDirectorySeparator(directory[index]))
            {
                continue;
            }

            int length = index - segmentStart;
            if ((length == 12 && string.Compare(directory, segmentStart, "node_modules", 0, length, StringComparison.OrdinalIgnoreCase) == 0)
                || (length == 4 && string.Compare(directory, segmentStart, ".bin", 0, length, StringComparison.OrdinalIgnoreCase) == 0))
            {
                return true;
            }

            segmentStart = index + 1;
        }

        return false;
    }

    /// <summary>
    /// Locates the specified file by searching through directories in the provided search path, skipping package directories, and returns the full path if found.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="searchPath">The search path.</param>
    /// <returns>The string? result.</returns>
    private static string? FindOnPath(string fileName, string? searchPath)
    {
        foreach (string directory in GetSearchDirectories(searchPath))
        {
            if (IsPackageDirectory(directory))
            {
                continue;
            }

            string path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses and validates a search path string, yielding fully qualified directory paths.
    /// </summary>
    /// <param name="searchPath">The search path.</param>
    /// <returns>A collection of ienumerable items.</returns>
    private static IEnumerable<string> GetSearchDirectories(string? searchPath)
    {
        if (searchPath is null || searchPath.Length == 0)
        {
            yield break;
        }

        foreach (string entry in searchPath.Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (!IsFullyQualifiedPath(directory))
            {
                // Only fully qualified entries are independent of the workspace/current drive.
                continue;
            }

            try
            {
                directory = Path.GetFullPath(directory);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                continue;
            }

            yield return directory;
        }
    }
}
