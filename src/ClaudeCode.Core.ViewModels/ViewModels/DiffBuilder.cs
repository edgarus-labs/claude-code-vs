using System;
using System.Collections.Generic;

namespace ClaudeCode.Core.ViewModels;

internal static class DiffBuilder
{
    private const long _maxAlignmentCells = 2_000_000;

    public static IReadOnlyList<DiffLineViewModel> Build(string oldText, string newText) => BuildCore(oldText, newText);

    private static List<DiffLineViewModel> BuildCore(string oldText, string newText)
    {
        // A terminating "\n" ends the last line rather than starting an empty one - but only when
        // both sides agree on it (or one side is empty): "one" vs "one\n" must still show the added
        // newline as a real difference (see Build_TrailingNewlineDifference_IsVisible).
        var stripTerminator = (oldText.Length == 0 || EndsWithNewline(oldText)) && (newText.Length == 0 || EndsWithNewline(newText));
        var oldLines = SplitLines(oldText, stripTerminator);
        var newLines = SplitLines(newText, stripTerminator);
        int n = oldLines.Length;
        int m = newLines.Length;

        var result = new List<DiffLineViewModel>(n + m);

        if (n == 0 || m == 0 || (long)n * m > _maxAlignmentCells)
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

    private static bool EndsWithNewline(string text) => text.Length > 0 && text[text.Length - 1] == '\n';

    private static string[] SplitLines(string text, bool stripTerminator)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // The terminator's empty trailing segment is dropped from the split, not cut from the text:
        // that avoids a whole-file copy, and "\n" correctly remains one empty line.
        if (stripTerminator && lines.Length > 1 && lines[lines.Length - 1].Length == 0) Array.Resize(ref lines, lines.Length - 1);
        return lines;
    }
}
