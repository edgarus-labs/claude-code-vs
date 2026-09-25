using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Finds the files under a workspace whose path ends in a given run of whole path segments - the
/// last resort for a transcript file reference no tool call reported (a name Claude only saw in a
/// shell command's output). Callers still confine every result with WorkspacePathGuard.
/// </summary>
internal static class WorkspaceFileSearch
{
    /// <summary>Entries visited before the walk gives up, so a huge tree cannot stall a click.</summary>
    private const int MaxEntries = 500_000;

    /// <summary>A build or tool copy of a source file there is not a second file the user means.</summary>
    private static readonly HashSet<string> CopyFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", "node_modules",
    };

    /// <summary>
    /// Returns the files whose full path ends in <paramref name="suffix"/> (a separator followed by
    /// the reference, separators normalized to <see cref="Path.DirectorySeparatorChar"/>): those
    /// outside build/tool folders when there are any, otherwise those inside them. The build/tool
    /// folders are walked only after the rest, so their size cannot keep a source file from being
    /// found. Git's object store and reparse points (junctions, symlinks) are not walked - the
    /// latter could lead out of the workspace or into a cycle. A walk that reaches the entry cap
    /// with fewer than two matches cannot tell a unique file from the first of several, so it
    /// throws <see cref="IOException"/> rather than return them.
    /// </summary>
    public static IReadOnlyList<string> FindBySuffix(string workspaceRoot, string suffix, CancellationToken cancellationToken) =>
        FindBySuffix(workspaceRoot, suffix, MaxEntries, cancellationToken);

    internal static IReadOnlyList<string> FindBySuffix(string workspaceRoot, string suffix, int maxEntries, CancellationToken cancellationToken)
    {
        var budget = maxEntries;
        var copyFolders = new List<string>();
        var sources = Walk(new[] { workspaceRoot }, suffix, copyFolders, ref budget, cancellationToken);
        return sources.Count > 0 ? sources : Walk(copyFolders, suffix, deferredCopyFolders: null, ref budget, cancellationToken);
    }

    // Walks the roots; a build/tool folder goes into deferredCopyFolders instead of being walked,
    // unless that is null (the roots already are such folders).
    private static List<string> Walk(IEnumerable<string> roots, string suffix, List<string>? deferredCopyFolders, ref int budget, CancellationToken cancellationToken)
    {
        var found = new List<string>();
        var pending = new Stack<string>(roots);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                continue;
            }

            var exhausted = false;
            try
            {
                foreach (var entry in entries)
                {
                    if (--budget < 0)
                    {
                        exhausted = true;
                        break;
                    }

                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (deferredCopyFolders is not null && CopyFolders.Contains(entry.Name))
                        {
                            deferredCopyFolders.Add(entry.FullName);
                        }
                        else
                        {
                            pending.Push(entry.FullName);
                        }
                    }
                    else if (entry.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                // A directory that vanished or locked mid-walk: keep what it yielded, go on.
            }

            if (exhausted)
            {
                // Two matches already make the reference ambiguous, however many more there are.
                return found.Count > 1
                    ? found
                    : throw new IOException("the workspace holds too many files to search for it; ask Claude for the full path.");
            }
        }

        return found;
    }
}
