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
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(executablePath);
        string extension = Path.GetExtension(fullPath);
        string directory = Path.GetDirectoryName(fullPath)!;
        string? scriptPath = null;
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

        string nodeName = _isWindows ? "node.exe" : "node";
        string? nodePath = Path.Combine(directory, nodeName);
        if (!File.Exists(nodePath))
        {
            nodePath = FindOnPath(nodeName, searchPath);
        }

        return nodePath is null ? null : new AcpExecutableSpec(nodePath, new[] { Path.GetFullPath(scriptPath) });
    }

    private static string? FindOnPath(string fileName, string? searchPath)
    {
        foreach (string directory in GetSearchDirectories(searchPath))
        {
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
            if (directory.Length == 0)
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
