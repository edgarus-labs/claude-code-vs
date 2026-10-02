using System.Globalization;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>Normalizes session titles to a single bounded line for display.</summary>
public static class SessionTitleFormat
{
    public const int MaxTitleLength = 80;

    private const int SessionIdPrefixLength = 8;

    /// <summary>The first usable line of <paramref name="title"/>, ellipsised at
    /// <see cref="MaxTitleLength"/>. With no usable title, falls back to the leading
    /// <c>8</c> characters of <paramref name="sessionId"/>'s first usable line, or the empty string
    /// when that is null too.</summary>
    public static string Describe(string? title, string? sessionId)
    {
        string line = SingleLine(title, MaxTitleLength);
        if (line.Length > 0) return line;

        string id = FirstUsableLine(sessionId);
        return id.Length <= SessionIdPrefixLength ? id : id.Substring(0, SessionIdPrefixLength);
    }

    internal static string SingleLine(string? text, int maxLength)
    {
        string line = FirstUsableLine(text);
        if (line.Length <= maxLength) return line;
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

    private static readonly char[] LineBreaks = { '\n', '\r', '\u0085', '\u2028', '\u2029' };

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

    private static bool IsControlOrBidi(char value) =>
        CharUnicodeInfo.GetUnicodeCategory(value) is UnicodeCategory.Control or UnicodeCategory.Format;
}
