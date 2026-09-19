using System.Globalization;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>The one normalization rule for a session title. <c>SessionSummary.Title</c> is
/// agent-reported and is bound into the single-row panel header, its tooltip and every history row,
/// where an embedded line break reflows the layout and a multi-megabyte string hangs WPF's measure
/// pass. Both presentation sites go through this so they cannot drift apart:
/// <c>ChatViewModel.NormalizeSessionTitle</c> for the header and <c>SessionTitleConverter</c> for
/// the row.</summary>
public static class SessionTitleFormat
{
    public const int MaxTitleLength = 80;

    private const int SessionIdPrefixLength = 8;

    /// <summary>The first usable line of <paramref name="title"/>, ellipsised at
    /// <see cref="MaxTitleLength"/>. With no usable title, falls back to the leading
    /// <c>8</c> characters of <paramref name="sessionId"/>'s first usable line - the id is
    /// agent-reported too, and lands in the same single-row surface - or the empty string when
    /// that is null too, which callers that own their own fallback (the panel header) pass.</summary>
    public static string Describe(string? title, string? sessionId)
    {
        string line = SingleLine(title, MaxTitleLength);
        if (line.Length > 0) return line;

        string id = FirstUsableLine(sessionId);
        return id.Length <= SessionIdPrefixLength ? id : id.Substring(0, SessionIdPrefixLength);
    }

    /// <summary>The same rule at a caller-chosen bound, for the other single-line surface that
    /// renders agent text: the attention notification. Returns the first line that still has
    /// content once control and bidi characters are removed, ellipsised at
    /// <paramref name="maxLength"/>, or the empty string when there is none.</summary>
    internal static string SingleLine(string? text, int maxLength)
    {
        string line = FirstUsableLine(text);
        if (line.Length <= maxLength) return line;
        // Cutting between the halves of a surrogate pair leaves a lone high surrogate that
        // TrimEnd will not remove and the text layout renders as a replacement box.
        int cut = maxLength - 1;
        if (char.IsHighSurrogate(line[cut - 1])) cut--;
        return line.Substring(0, cut).TrimEnd() + "…";
    }

    private static string FirstUsableLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        foreach (string rawLine in text!.Split(LineBreaks))
        {
            string line = StripControlAndBidi(rawLine).Trim();
            if (line.Length > 0) return line;
        }

        return string.Empty;
    }

    // Every one of these breaks a line in WPF text layout, NEL (U+0085) included, so an agent
    // title containing any of them would reflow a single-row header or history row.
    private static readonly char[] LineBreaks = { '\n', '\r', '\u0085', '\u2028', '\u2029' };

    // Trim only reaches the ends. Interior C0/C1 controls and bidi overrides survive into the
    // header, its tooltip and the notification text, where U+202E silently reverses the rest of
    // the line; nothing downstream makes a decision on the title, so removing them is enough.
    // Shared with ToolDisplayName so the tool-title consent surface strips the same set.
    internal static string StripControlAndBidi(string line)
    {
        int first = -1;
        for (int index = 0; index < line.Length; index++)
        {
            if (!IsControlOrBidi(line[index])) continue;
            first = index;
            break;
        }

        if (first < 0) return line;
        var kept = new StringBuilder(line.Length - 1);
        kept.Append(line, 0, first);
        for (int index = first + 1; index < line.Length; index++)
        {
            if (!IsControlOrBidi(line[index])) kept.Append(line[index]);
        }

        return kept.ToString();
    }

    // Format (Cf) covers every bidi control (U+061C, U+200E/F, U+202A-E, U+2066-9) and the
    // zero-width characters (U+200B-D, U+2060, U+FEFF); none of them is whitespace, so a title
    // made only of them would otherwise pass as content and blank the row with no fallback.
    private static bool IsControlOrBidi(char value) =>
        CharUnicodeInfo.GetUnicodeCategory(value) is UnicodeCategory.Control or UnicodeCategory.Format;
}
