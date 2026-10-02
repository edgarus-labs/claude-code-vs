using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCode.Acp;

public static class AcpExecutableResolver
{
    private static readonly bool _isWindows = Path.DirectorySeparatorChar == '\\';
    private static readonly string[] _acpAdapterCandidateNames = _isWindows
        ? new[] { "claude-agent-acp.exe", "claude-agent-acp.cmd" }
        : new[] { "claude-agent-acp" };
    private static readonly string _nodeExecutableName = _isWindows ? "node.exe" : "node";

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

        return (path!.Length >= 2 && IsDirectorySeparator(path[0]) && IsDirectorySeparator(path[1]))
            || (path.Length >= 3 && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))
                && path[1] == ':' && IsDirectorySeparator(path[2]));
    }

    public static AcpExecutableSpec? TryResolveDefault() => TryResolveDefault(Environment.GetEnvironmentVariable("PATH"));

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

    public static AcpExecutableSpec? TryResolve(string executablePath) =>
        TryResolve(executablePath, Environment.GetEnvironmentVariable("PATH"));

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

            preferAdjacentNode = !IsPackageDirectory(directory);
        }
        else if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            if (!fullPath.Replace('\\', '/').EndsWith("/@agentclientprotocol/claude-agent-acp/dist/index.js", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            scriptPath = fullPath;
        }
        else
        {
            string adapterName = _isWindows ? "claude-agent-acp.exe" : "claude-agent-acp";
            if (!Path.GetFileName(fullPath).Equals(adapterName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new AcpExecutableSpec(fullPath, Array.Empty<string>());
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

        return nodePath is null ? null : new AcpExecutableSpec(nodePath, new[] { Path.GetFullPath(scriptPath) });
    }

    /// <summary>Finds a Node runtime on <paramref name="searchPath"/>, considering only fully qualified
    /// entries that are not package directories. Returns null when none is found.</summary>
    public static string? FindNodeOnPath(string? searchPath) => FindOnPath(_nodeExecutableName, searchPath);

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    /// <summary>
    /// Determines whether the specified directory path contains a segment named “node_modules” or “.bin”, indicating a package directory.
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
    /// Searches the provided directories for the specified file name, returning the first matching full path or null if the file is not found.
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
