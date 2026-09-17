using System;
using System.Collections.Generic;

namespace ClaudeCode.Core.ViewModels
{
    /// <summary>
    /// Builds a simple line-level unified diff for tool-call content. Not a full Myers/LCS diff widget -
    /// good enough for the small file-edit snippets tool calls actually stream.
    /// </summary>
    internal static class DiffBuilder
    {
        /// <summary>Above this many (oldLines * newLines) cells we skip alignment and just show whole-file remove+add.</summary>
        private const long MaxAlignmentCells = 2_000_000;

        public static IReadOnlyList<DiffLineViewModel> Build(string oldText, string newText)
        {
            var oldLines = SplitLines(oldText);
            var newLines = SplitLines(newText);
            int n = oldLines.Length;
            int m = newLines.Length;

            var result = new List<DiffLineViewModel>(n + m);

            if ((long)n * m > MaxAlignmentCells)
            {
                foreach (var line in oldLines)
                {
                    result.Add(new DiffLineViewModel(DiffLineKind.Removed, line));
                }

                foreach (var line in newLines)
                {
                    result.Add(new DiffLineViewModel(DiffLineKind.Added, line));
                }

                return result;
            }

            // Longest-common-subsequence table, built backwards so we can greedily walk forward below.
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    lcs[i, j] = oldLines[i] == newLines[j]
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int a = 0, b = 0;
            while (a < n && b < m)
            {
                if (oldLines[a] == newLines[b])
                {
                    result.Add(new DiffLineViewModel(DiffLineKind.Context, oldLines[a]));
                    a++;
                    b++;
                }
                else if (lcs[a + 1, b] >= lcs[a, b + 1])
                {
                    result.Add(new DiffLineViewModel(DiffLineKind.Removed, oldLines[a]));
                    a++;
                }
                else
                {
                    result.Add(new DiffLineViewModel(DiffLineKind.Added, newLines[b]));
                    b++;
                }
            }

            while (a < n)
            {
                result.Add(new DiffLineViewModel(DiffLineKind.Removed, oldLines[a]));
                a++;
            }

            while (b < m)
            {
                result.Add(new DiffLineViewModel(DiffLineKind.Added, newLines[b]));
                b++;
            }

            return result;
        }

        private static string[] SplitLines(string text) => (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
    }
}
