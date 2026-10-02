using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Provides functionality to search for files within a workspace, supporting suffix filtering, optional folder copying, and limiting the number of returned entries.
/// </summary>
internal static class WorkspaceFileSearch
{
    /// <summary>
    /// The max entries.
    /// </summary>
    private const int _maxEntries = 500_000;

    private static readonly HashSet<string> _copyFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", "node_modules",
    };

    public static IReadOnlyList<string> FindBySuffix(string workspaceRoot, string suffix, CancellationToken cancellationToken) =>
        FindBySuffix(workspaceRoot, suffix, _maxEntries, cancellationToken);

    /// <summary>
    /// Searches the specified workspace root for paths ending with the given suffix, returning up to the requested maximum number of entries and, if needed, continues the search in any discovered copy folders.
    /// </summary>
    /// <param name="workspaceRoot">The workspace root.</param>
    /// <param name="suffix">The suffix.</param>
    /// <param name="maxEntries">The max entries.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A collection of iread only list items.</returns>
    internal static IReadOnlyList<string> FindBySuffix(string workspaceRoot, string suffix, int maxEntries, CancellationToken cancellationToken)
    {
        var budget = maxEntries;
        var copyFolders = new List<string>();
        var sources = Walk(new[] { workspaceRoot }, suffix, copyFolders, ref budget, cancellationToken);
        return sources.Count > 0 ? sources : Walk(copyFolders, suffix, deferredCopyFolders: null, ref budget, cancellationToken);
    }

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

                        if (deferredCopyFolders is not null && _copyFolders.Contains(entry.Name))
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
            }

            if (exhausted)
            {
                return found.Count > 1
                    ? found
                    : throw new IOException("the workspace holds too many files to search for it; ask Claude for the full path.");
            }
        }

        return found;
    }
}
