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
        var oldLines = SplitLines(oldText, stripTerminator, out int n);
        var newLines = SplitLines(newText, stripTerminator, out int m);

        var result = new List<DiffLineViewModel>(n + m);

        if (n == 0 || m == 0 || (long)n * m > _maxAlignmentCells)
        {
            for (int i = 0; i < n; i++)
            {
                result.Add(new DiffLineViewModel(DiffLineKind.Removed, oldLines[i]));
            }

            for (int j = 0; j < m; j++)
            {
                result.Add(new DiffLineViewModel(DiffLineKind.Added, newLines[j]));
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

    // Hands back the raw split array plus the number of leading entries that are real lines. The
    // count exists because shrinking the array to drop the terminator's empty trailing segment is
    // not an in-place operation: it allocates a second full-size array and copies every element,
    // which every diff of a normally terminated file would pay. Entries at or past "count" are
    // not lines and must not be read.
    private static string[] SplitLines(string text, bool stripTerminator, out int count)
    {
        if (string.IsNullOrEmpty(text))
        {
            count = 0;
            return Array.Empty<string>();
        }

        // The terminator's empty trailing segment is discounted after the split rather than trimmed
        // off the text first: that avoids a second whole-file string copy. The length guard keeps
        // "\n" as one empty line.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        count = lines.Length;
        if (stripTerminator && count > 1 && lines[count - 1].Length == 0) count--;
        return lines;
    }
}
