using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCode.Contracts;

/// <summary>
/// Confines agent-supplied file paths to a workspace root. Used everywhere an untrusted ACP agent
/// or MCP tool call supplies a <c>path</c> that must not escape the open workspace (file broker,
/// VsControl document tools).
/// </summary>
public static class WorkspacePathGuard
{
    /// <summary>Acquires a confined file operation; dispose it after all asynchronous work completes.</summary>
    public static WorkspacePathLease AcquireFile(string? workspaceRoot, string? candidatePath) =>
        WorkspacePathLease.Acquire(workspaceRoot, candidatePath, document: false);

    /// <summary>
    /// Pins an existing Windows document and its ancestors against replacement for VS path-based APIs.
    /// Reparse points and missing files are rejected rather than reopening an unprotected path.
    /// </summary>
    public static WorkspacePathLease AcquireDocument(string? workspaceRoot, string? candidatePath) =>
        WorkspacePathLease.Acquire(workspaceRoot, candidatePath, document: true);

    /// <summary>
    /// Performs a point-in-time containment check, including existing symlink targets.
    /// This does not authorize later path-based I/O: use <see cref="AcquireFile"/> or
    /// <see cref="AcquireDocument"/> to retain protection through the operation.
    /// On Windows the reparse resolution addresses a path at or beyond MAX_PATH (260) through the
    /// <c>\\?\</c> device form, so the answer depends only on the filesystem — never on the host's
    /// long-path configuration and never on whether the leaf exists yet.
    /// </summary>
    /// <returns><c>true</c> and the resolved absolute path when containment holds; otherwise <c>false</c>.</returns>
    public static bool TryResolveWithinWorkspace(string? workspaceRoot, string? candidatePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(candidatePath))
        {
            return false;
        }

        string nonNullCandidatePath = candidatePath!;

        // Reject UNC (\\server\share\...) and device-namespace (\\?\..., \\.\...) forms outright:
        // GetFullPath would happily resolve them, but they never denote a path under a local root.
        // Win32 accepts '/' as a separator, so "//server/share" and "/\server\share" are the same
        // UNC form as "\\server\share" and have to be refused identically. On Linux a leading "//"
        // is an ordinary absolute path, so only the backslash spelling is refused there.
        if (nonNullCandidatePath.StartsWith(@"\\", StringComparison.Ordinal)
            || (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && nonNullCandidatePath.Length >= 2
                && IsWindowsSeparator(nonNullCandidatePath[0])
                && IsWindowsSeparator(nonNullCandidatePath[1])))
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
        if (!TryGetCanonicalPathOrNearestAncestor(resolvedCandidate, out string canonicalCandidate)
            || !TryGetCanonicalPathOrNearestAncestor(resolvedRoot, out string canonicalRoot)
            || !IsWithinRoot(canonicalCandidate, canonicalRoot))
        {
            return false;
        }

        fullPath = resolvedCandidate;
        return true;
    }

    private static bool IsWithinRoot(string resolvedCandidate, string resolvedRoot)
    {
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(resolvedCandidate, resolvedRoot, comparison))
        {
            return true;
        }

        // Trailing separator on the root is mandatory here: without it, "C:\repo-secret" would pass
        // a naive StartsWith("C:\repo") check even though it is a sibling directory, not a child.
        string rootWithSeparator = resolvedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;

        return resolvedCandidate.StartsWith(rootWithSeparator, comparison);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> (or its nearest existing ancestor, when the leaf and
    /// possibly further ancestors do not exist yet) to its filesystem-resolved final path.
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

            // Only a genuinely absent component may be represented by an ancestor. Permission
            // failures and dangling links are not evidence that the path is safe.
            int error = Marshal.GetLastWin32Error();
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if ((windows && !IsAbsentOnWindows(current, error))
                || (!windows && (error != 2 || ReadLink(GetUnixPathBytes(current), new byte[1], new UIntPtr(1)).ToInt64() >= 0)))
            {
                canonical = string.Empty;
                return false;
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

    /// <summary>
    /// Distinguishes a component that does not exist from a dangling reparse point. CreateFileW
    /// without FILE_FLAG_OPEN_REPARSE_POINT follows the reparse data, so a junction or symlink
    /// whose target is missing fails with the same ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND as
    /// an absent entry — yet the link itself exists and can be repointed anywhere at any time.
    /// GetFileAttributesW reports the link's own attributes without following it.
    /// </summary>
    private static bool IsAbsentOnWindows(string path, int openError)
    {
        if (openError != 2 && openError != 3)
        {
            return false;
        }

        if (GetFileAttributesW(LongPathSafe(path)) != InvalidFileAttributes)
        {
            return false;
        }

        int attributeError = Marshal.GetLastWin32Error();
        return attributeError == 2 || attributeError == 3;
    }

    /// <summary>
    /// Renders a path for the raw Win32 entry points used here. Both bypass the System.IO
    /// long-path shim, and without the <c>\\?\</c> prefix Win32 normalization rejects a path at or
    /// beyond MAX_PATH with ERROR_PATH_NOT_FOUND — the very error an absent component reports —
    /// unless the host happens to have LongPathsEnabled set. Addressing the object through the
    /// device form removes that ambiguity on every host, so errors 2 and 3 always prove the
    /// component really is missing and absence never has to be assumed. UNC candidates are refused
    /// before they reach here and would need the <c>\\?\UNC\</c> spelling rather than a bare
    /// prefix, so they are left untouched and keep failing closed.
    /// </summary>
    private static string LongPathSafe(string path) =>
        path.Length < MaxPath || path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    private static bool IsWindowsSeparator(char value) => value == '\\' || value == '/';

    private static bool TryGetFinalPath(string path, out string finalPath)
    {
        finalPath = string.Empty;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
            IntPtr resolved = RealPath(GetUnixPathBytes(path), IntPtr.Zero);
            if (resolved == IntPtr.Zero) return false;
            try
            {
                finalPath = Marshal.PtrToStringAnsi(resolved)!;
                return true;
            }
            finally
            {
                Free(resolved);
            }
        }

        using SafeFileHandle handle = CreateFileW(
            LongPathSafe(path),
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
    private const int MaxPath = 260;
    private const uint InvalidFileAttributes = 0xFFFFFFFF;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFileAttributesW(string lpFileName);

    internal static byte[] GetUnixPathBytes(string path)
    {
        if (path.IndexOf('\0') >= 0) throw new ArgumentException("Filesystem paths cannot contain NUL.", nameof(path));
        var bytes = new byte[Encoding.UTF8.GetByteCount(path) + 1];
        Encoding.UTF8.GetBytes(path, 0, path.Length, bytes, 0);
        return bytes;
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath(byte[] path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static extern IntPtr ReadLink(byte[] path, byte[] buffer, UIntPtr bufferSize);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
