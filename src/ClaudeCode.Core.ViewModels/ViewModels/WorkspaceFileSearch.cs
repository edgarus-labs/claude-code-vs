using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

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
    /// outside build/tool folders when there are any, otherwise those inside them. Git's object
    /// store and reparse points (junctions, symlinks) are not walked - the latter could lead out of
    /// the workspace or into a cycle.
    /// </summary>
    public static IReadOnlyList<string> FindBySuffix(string workspaceRoot, string suffix)
    {
        var sources = new List<string>();
        var copies = new List<string>();
        var pending = new Stack<(string Path, bool IsCopyFolder)>();
        pending.Push((workspaceRoot, false));
        var visited = 0;

        while (pending.Count > 0)
        {
            var (directory, isCopyFolder) = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                continue;
            }

            try
            {
                foreach (var entry in entries)
                {
                    if (++visited > MaxEntries)
                    {
                        return sources.Count > 0 ? sources : copies;
                    }

                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        if (!entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                        {
                            pending.Push((entry.FullName, isCopyFolder || CopyFolders.Contains(entry.Name)));
                        }
                    }
                    else if (entry.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        (isCopyFolder ? copies : sources).Add(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                // A directory that vanished or locked mid-walk: keep what it yielded, go on.
            }
        }

        return sources.Count > 0 ? sources : copies;
    }
}
