using System;
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

        return markdown.Substring(0, maxLength) +
            "\n\n*(message truncated: exceeded the maximum renderable size)*";
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
    /// Scales the streaming re-render throttle with the current text length: short messages stay
    /// snappy at 100ms, very long ones back off up to 1000ms so re-parsing large documents on every
    /// tick cannot starve the UI thread.
    /// </summary>
    public static TimeSpan ComputeRenderInterval(int textLength) =>
        TimeSpan.FromMilliseconds(textLength < 8000 ? 100 : Math.Min(1000, textLength / 80));

    /// <summary>
    /// True only for absolute http/https links whose host is not loopback (localhost/127.0.0.1/[::1])
    /// and not the all-zeroes address 0.0.0.0, which <see cref="Uri.IsLoopback"/> does not classify
    /// as loopback even though it routes to the local host on most platforms.
    /// </summary>
    public static bool IsNavigableLink(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        !string.IsNullOrEmpty(uri.Host) &&
        !uri.IsLoopback &&
        !string.Equals(uri.Host, "0.0.0.0", StringComparison.OrdinalIgnoreCase);
}
