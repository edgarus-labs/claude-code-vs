using System;
using System.Collections.Generic;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Represents a utility for building and comparing text differences with configurable alignment constraints.
/// </summary>
internal static class DiffBuilder
{
    /// <summary>
    /// The max alignment cells.
    /// </summary>
    private const long _maxAlignmentCells = 2_000_000;

    /// <summary>
    /// Generates a read-only list of diff line view models comparing the old and new text.
    /// </summary>
    /// <param name="oldText">The old text.</param>
    /// <param name="newText">The new text.</param>
    /// <returns>A collection of iread only list items.</returns>
    public static IReadOnlyList<DiffLineViewModel> Build(string oldText, string newText) => BuildCore(oldText, newText);

    /// <summary>
    /// Constructs a list of line-by-line differences between two text inputs, handling trailing newlines consistently and applying the longest common subsequence algorithm for optimal diff generation.
    /// </summary>
    /// <param name="oldText">The old text.</param>
    /// <param name="newText">The new text.</param>
    /// <returns>A collection of list items.</returns>
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

    /// <summary>
    /// Determines whether the specified text ends with a newline character.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>true if the operation succeeded; otherwise, false.</returns>
    private static bool EndsWithNewline(string text) => text.Length > 0 && text[text.Length - 1] == '\n';

    // Hands back the raw split array plus the number of leading entries that are real lines. The
    // count exists because shrinking the array to drop the terminator's empty trailing segment is
    // not an in-place operation: it allocates a second full-size array and copies every element,
    // which every diff of a normally terminated file would pay. Entries at or past "count" are
    // not lines and must not be read.
    /// <summary>
    /// Splits the input text into lines while preserving line endings and returning the count of valid lines.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="stripTerminator">The strip terminator.</param>
    /// <param name="count">The count.</param>
    /// <returns>A collection of string items.</returns>
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
