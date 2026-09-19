namespace ClaudeCode.Core.ViewModels;

/// <summary>The one normalization rule for a session title. <c>SessionSummary.Title</c> is
/// agent-reported and is bound into the single-row panel header, its tooltip and every history row,
/// where an embedded line break reflows the layout and a multi-megabyte string hangs WPF's measure
/// pass. Both presentation sites go through this so they cannot drift apart.</summary>
public static class SessionTitleFormat
{
    public const int MaxTitleLength = 80;

    private const int SessionIdPrefixLength = 8;

    /// <summary>The first non-empty trimmed line of <paramref name="title"/>, ellipsised at
    /// <see cref="MaxTitleLength"/>. With no usable title, falls back to the leading
    /// <c>8</c> characters of <paramref name="sessionId"/> - or the empty string when that is null
    /// too, which callers that own their own fallback (the panel header) pass.</summary>
    public static string Describe(string? title, string? sessionId)
    {
        string line = FirstNonEmptyLine(title);
        if (line.Length > 0)
        {
            return line.Length <= MaxTitleLength ? line : line.Substring(0, MaxTitleLength - 1).TrimEnd() + "…";
        }

        string id = sessionId ?? string.Empty;
        return id.Length <= SessionIdPrefixLength ? id : id.Substring(0, SessionIdPrefixLength);
    }

    // Every one of these breaks a line in WPF text layout, NEL (U+0085) included, so an agent
    // title containing any of them would reflow a single-row header or history row.
    private static readonly char[] LineBreaks = { '\n', '\r', '\u0085', '\u2028', '\u2029' };

    private static string FirstNonEmptyLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        foreach (string line in text!.Split(LineBreaks))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return string.Empty;
    }
}
