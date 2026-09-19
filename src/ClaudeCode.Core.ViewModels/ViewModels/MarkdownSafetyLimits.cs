using System;
using System.Net;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Pure string/Uri guardrails for rendering untrusted assistant Markdown in
/// <c>ClaudeCode.Core.Views.MarkdownMessageView</c>. Kept here (XAML-free) so they are directly
/// unit-testable; the WPF view calls into these instead of hosting the logic itself.
/// </summary>
public static class MarkdownSafetyLimits
{
    public const int MaxMarkdownLength = 200_000;
    public const int MaxBlockquoteDepth = 20;
    internal const string TruncationNotice = "\n\n*(message truncated: exceeded the maximum renderable size)*";

    /// <summary>
    /// Truncates markdown text before it reaches Markdig, bounding parser work and rendered DOM
    /// size for arbitrarily large model output.
    /// </summary>
    public static string LimitMarkdownLength(string markdown, int maxLength = MaxMarkdownLength)
    {
        if (markdown.Length <= maxLength)
        {
            return markdown;
        }

        return markdown.Substring(0, maxLength) + TruncationNotice;
    }

    /// <summary>
    /// Caps consecutive leading '&gt;' blockquote markers per line so a pathological input cannot
    /// force Markdig to build an arbitrarily deep nested block tree.
    /// </summary>
    public static string LimitBlockquoteNesting(string markdown, int maxDepth = MaxBlockquoteDepth)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown;
        }

        var lines = markdown.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            {
                indent++;
            }

            var depth = 0;
            var markerEnd = indent;
            while (markerEnd < line.Length && line[markerEnd] == '>')
            {
                depth++;
                markerEnd++;
                if (markerEnd < line.Length && line[markerEnd] == ' ')
                {
                    markerEnd++;
                }
            }

            if (depth <= maxDepth)
            {
                continue;
            }

            var kept = new StringBuilder(line.Substring(0, indent));
            for (var d = 0; d < maxDepth; d++)
            {
                kept.Append("> ");
            }
            kept.Append(line, markerEnd, line.Length - markerEnd);
            lines[i] = kept.ToString();
            changed = true;
        }

        return changed ? string.Join("\n", lines) : markdown;
    }

    /// <summary>
    /// Inserts a blank line before any ``` or ~~~ fence that opens directly after a non-blank line.
    /// CommonMark only starts a fenced code block there when it follows a blank line (or the start
    /// of the document); otherwise it is "lazy continuation" of the preceding paragraph and renders
    /// as literal text, including the fence markers themselves. Session-resume replay text (from the
    /// external CLI, not this extension) sometimes omits that blank line before quoting tool output,
    /// so this compensates rather than showing the raw ``` marker to the user.
    /// </summary>
    public static string EnsureBlankLineBeforeFences(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || markdown.IndexOf("```", StringComparison.Ordinal) < 0
            && markdown.IndexOf("~~~", StringComparison.Ordinal) < 0)
        {
            return markdown;
        }

        var lines = markdown.Split('\n');
        var result = new System.Collections.Generic.List<string>(lines.Length + 4);
        var insideFence = false;
        foreach (var line in lines)
        {
            if (IsFenceMarkerLine(line))
            {
                if (!insideFence && result.Count > 0 && result[result.Count - 1].Trim().Length > 0)
                {
                    result.Add(string.Empty);
                }

                insideFence = !insideFence;
            }

            result.Add(line);
        }

        return string.Join("\n", result);
    }

    private static bool IsFenceMarkerLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    /// <summary>
    /// Scales the streaming re-render throttle with the current text length: short messages stay
    /// snappy at 100ms, very long ones back off up to 1000ms so re-parsing large documents on every
    /// tick cannot starve the UI thread.
    /// </summary>
    public static TimeSpan ComputeRenderInterval(int textLength) =>
        TimeSpan.FromMilliseconds(textLength < 8000 ? 100 : Math.Min(1000, textLength / 80));

    /// <summary>
    /// True only for absolute http/https links whose host is neither loopback nor an unspecified
    /// IP address. IPv4-mapped IPv6 addresses are checked as IPv4 destinations.
    /// </summary>
    public static bool IsNavigableLink(Uri uri)
    {
        if ((uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrEmpty(uri.Host) || uri.IsLoopback)
        {
            return false;
        }

        if (!IPAddress.TryParse(uri.DnsSafeHost, out var address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return !IPAddress.IsLoopback(address) &&
            !address.Equals(IPAddress.Any) &&
            !address.Equals(IPAddress.IPv6Any);
    }
}
