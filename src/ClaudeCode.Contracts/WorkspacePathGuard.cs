using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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

        if (!IsWithinRoot(resolvedCandidate, resolvedRoot))
        {
            return false;
        }

        // Path.GetFullPath is purely lexical and never follows NTFS reparse points. A junction or
        // symlink living inside the workspace can retarget any descendant path to a location outside
        // it, so re-check containment against the reparse-resolved ("canonical") form of both paths.
        // When a segment does not exist yet (e.g. a new file about to be written), canonicalize the
        // nearest existing ancestor instead, and fail closed if no ancestor can be resolved at all.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryGetCanonicalPathOrNearestAncestor(resolvedCandidate, out string canonicalCandidate)
                || !TryGetCanonicalPathOrNearestAncestor(resolvedRoot, out string canonicalRoot)
                || !IsWithinRoot(canonicalCandidate, canonicalRoot))
            {
                return false;
            }
        }

        fullPath = resolvedCandidate;
        return true;
    }

    private static bool IsWithinRoot(string resolvedCandidate, string resolvedRoot)
    {
        if (string.Equals(resolvedCandidate, resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Trailing separator on the root is mandatory here: without it, "C:\repo-secret" would pass
        // a naive StartsWith("C:\repo") check even though it is a sibling directory, not a child.
        string rootWithSeparator = resolvedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;

        return resolvedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> (or its nearest existing ancestor, when the leaf and
    /// possibly further ancestors do not exist yet) to its NTFS reparse-resolved final path.
    /// </summary>
    private static bool TryGetCanonicalPathOrNearestAncestor(string path, out string canonical)
    {
        string current = path;
        string suffix = string.Empty;

        while (true)
        {
            if (TryGetFinalPath(current, out string resolvedCurrent))
            {
                canonical = suffix.Length == 0
                    ? resolvedCurrent
                    : resolvedCurrent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + suffix;
                return true;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                // Not even the root could be opened (e.g. a drive that does not exist): canonicalization
                // is impossible, so the caller must fail closed rather than trust the lexical path.
                canonical = string.Empty;
                return false;
            }

            string segment = current.Substring(parent.Length).TrimStart(Path.DirectorySeparatorChar);
            suffix = suffix.Length == 0 ? segment : segment + Path.DirectorySeparatorChar + suffix;
            current = parent;
        }
    }

    private static bool TryGetFinalPath(string path, out string finalPath)
    {
        finalPath = string.Empty;

        using SafeFileHandle handle = CreateFileW(
            path,
            dwDesiredAccess: 0,
            dwShareMode: FileShareReadWriteDelete,
            lpSecurityAttributes: IntPtr.Zero,
            dwCreationDisposition: OpenExisting,
            dwFlagsAndAttributes: FileFlagBackupSemantics,
            hTemplateFile: IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return false;
        }

        var buffer = new char[short.MaxValue];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, dwFlags: 0);
        if (length == 0 || length >= buffer.Length)
        {
            return false;
        }

        finalPath = new string(buffer, 0, (int)length);
        return true;
    }

    private const uint FileShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
