using System;
using System.IO;

namespace ClaudeCode.Contracts;

/// <summary>
/// Confines agent-supplied file paths to a workspace root. Used everywhere an untrusted ACP agent
/// or MCP tool call supplies a <c>path</c> that must not escape the open workspace (file broker,
/// VsControl document tools).
/// </summary>
public static class WorkspacePathGuard
{
    /// <summary>
    /// Resolves <paramref name="candidatePath"/> to an absolute path and verifies it is
    /// <paramref name="workspaceRoot"/> itself or lies strictly underneath it. Rejects UNC paths
    /// (<c>\\server\share</c>) and Win32 device-namespace paths (<c>\\?\</c>, <c>\\.\</c>) outright,
    /// since those never denote a location under a local directory root.
    /// </summary>
    /// <returns><c>true</c> and the resolved absolute path when containment holds; otherwise <c>false</c>.</returns>
    public static bool TryResolveWithinWorkspace(string? workspaceRoot, string? candidatePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(candidatePath))
        {
            return false;
        }

        string nonNullCandidatePath = candidatePath!;

        // Reject UNC (\\server\share\...) and device-namespace (\\?\..., \\.\...) forms outright:
        // GetFullPath would happily resolve them, but they never denote a path under a local root.
        if (nonNullCandidatePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        string resolvedCandidate;
        string resolvedRoot;
        try
        {
            resolvedCandidate = Path.GetFullPath(nonNullCandidatePath);
            resolvedRoot = Path.GetFullPath(workspaceRoot!);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return false;
        }

        if (string.Equals(resolvedCandidate, resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = resolvedCandidate;
            return true;
        }

        // Trailing separator on the root is mandatory here: without it, "C:\repo-secret" would pass
        // a naive StartsWith("C:\repo") check even though it is a sibling directory, not a child.
        string rootWithSeparator = resolvedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;

        if (!resolvedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = resolvedCandidate;
        return true;
    }
}
