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

        // Rooted paths such as C:tools and \tools still depend on the current directory or drive.
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

    /// <summary>Finds a Node runtime on <paramref name="searchPath"/> using the same trusted filter
    /// the adapter launch uses: only fully qualified entries (never the current drive or directory)
    /// and never a package directory, whose content a workspace can plant. Returns null when no
    /// trusted entry holds one.</summary>
    public static string? FindNodeOnPath(string? searchPath) => FindOnPath(_nodeExecutableName, searchPath);

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

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
