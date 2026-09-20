using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Turns file-like tokens in assistant-authored Markdown into clickable links the WebView2
/// transcript can post back to the host, and parses those links back into a path/line pair. The
/// markdown is untrusted model output, so this never uses regular expressions for the scan - a
/// hand-rolled, single-pass, linear-time scanner (the caller already bounds input length via
/// <see cref="MarkdownSafetyLimits.MaxMarkdownLength"/>) avoids any catastrophic-backtracking
/// exposure. Fenced/indented code blocks, existing markdown links and images, and
/// autolinked/bare URLs are all left untouched so the rewrite never nests inside content that
/// already has its own destination.
/// </summary>
public static class ChatFileReference
{
    /// <summary>Href prefix of a transcript file-reference link.</summary>
    public const string LinkPrefix = "/__claudecode/open?";

    /// <summary>
    /// Extensions that make a token "look like a file" for linkification. A fixed allow-list
    /// (rather than "any token with a dot") is what keeps version-ish or member-access-ish
    /// backtick content such as <c>1.0.0</c>, <c>v1.2</c>, or <c>list.Add</c> from being mistaken
    /// for a file, and what keeps a bare word like "Node.js" alone in prose from being linkified.
    /// </summary>
    private static readonly HashSet<string> RecognizedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cs", "csx", "csproj", "vbproj", "fsproj", "sln", "vb", "fs", "fsx",
        "ts", "tsx", "js", "jsx", "mjs", "cjs",
        "json", "jsonc", "xml", "xaml", "resx", "config", "props", "targets",
        "yml", "yaml", "toml", "ini",
        "md", "mdx", "txt",
        "html", "htm", "css", "scss", "less",
        "py", "rb", "go", "rs", "java", "kt", "c", "h", "cpp", "cc", "hpp", "cxx",
        "sh", "ps1", "psm1", "bat", "cmd", "sql",
        "cshtml", "razor", "vue", "svelte", "php",
    };

    /// <summary>
    /// Rewrites file references into transcript links. Scans line by line so fenced (<c>```</c>)
    /// and 4-space/tab-indented code blocks can be skipped outright; within a remaining line,
    /// existing markdown links/images (<c>[...](...)</c>, <c>![...](...)</c>) and autolinked or
    /// bare URLs are copied through untouched, a backtick code span becomes a link only when its
    /// entire content is path-shaped, and a plain-prose word becomes a link when it carries a
    /// recognized file extension together with either a path separator or a <c>:line</c>,
    /// <c>:line:col</c>, or <c>(line,col)</c> location suffix. Trailing sentence punctuation is
    /// kept outside the link, and a literal backslash in the visible link text is doubled so it
    /// survives markdown rendering as a literal character rather than an escape.
    /// </summary>
    public static string LinkifyFileReferences(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var lines = markdown!.Split('\n');
        var inFencedBlock = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFencedBlock = !inFencedBlock;
                continue;
            }

            if (inFencedBlock || IsIndentedCodeLine(line))
            {
                continue;
            }

            lines[i] = LinkifyLine(line);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Parses a transcript file-reference link back into a path and optional line. Returns
    /// <see langword="false"/> for anything that is not this app's own <see cref="LinkPrefix"/>
    /// with a non-empty <c>path</c> query value - the host re-parses the raw href itself rather
    /// than trusting split path/line fields from the WebView2 page, since that page renders
    /// untrusted agent markdown. An unusable <c>line</c> value (missing, non-numeric,
    /// zero/negative, or too large to fit an <see cref="int"/>) still yields the path, just
    /// without a line: the file can still open.
    /// </summary>
    public static bool TryParseLink(string? href, out string path, out int? line)
    {
        path = string.Empty;
        line = null;

        if (href is null || !href.StartsWith(LinkPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var query = href.Substring(LinkPrefix.Length);
        string? rawPath = null;
        string? rawLine = null;

        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            var key = eq >= 0 ? pair.Substring(0, eq) : pair;
            var value = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;

            if (key == "path")
            {
                rawPath = value;
            }
            else if (key == "line")
            {
                rawLine = value;
            }
        }

        if (string.IsNullOrEmpty(rawPath))
        {
            return false;
        }

        path = Uri.UnescapeDataString(rawPath!);
        if (path.Length == 0)
        {
            return false;
        }

        if (rawLine is not null)
        {
            line = ParsePositiveLine(Uri.UnescapeDataString(rawLine));
        }

        return true;
    }

    private static bool IsIndentedCodeLine(string line)
    {
        if (line.Length > 0 && line[0] == '\t')
        {
            return true;
        }

        return line.Length >= 4 && line[0] == ' ' && line[1] == ' ' && line[2] == ' ' && line[3] == ' ';
    }

    private static string LinkifyLine(string line)
    {
        var result = new StringBuilder(line.Length);
        var i = 0;

        while (i < line.Length)
        {
            var c = line[i];

            if (c == '!' && i + 1 < line.Length && line[i + 1] == '[')
            {
                var imageConsumed = TryConsumeMarkdownLink(line, i + 1, out var image);
                if (imageConsumed > 0)
                {
                    result.Append('!').Append(image);
                    i += 1 + imageConsumed;
                    continue;
                }
            }

            if (c == '[')
            {
                var linkConsumed = TryConsumeMarkdownLink(line, i, out var link);
                if (linkConsumed > 0)
                {
                    result.Append(link);
                    i += linkConsumed;
                    continue;
                }
            }

            if (c == '<')
            {
                var autolinkConsumed = TryConsumeAutolink(line, i, out var autolink);
                if (autolinkConsumed > 0)
                {
                    result.Append(autolink);
                    i += autolinkConsumed;
                    continue;
                }
            }

            if (IsUrlStart(line, i))
            {
                var urlConsumed = ConsumeUrlLength(line, i);
                result.Append(line, i, urlConsumed);
                i += urlConsumed;
                continue;
            }

            if (c == '`')
            {
                var spanConsumed = TryConsumeCodeSpan(line, i, out var span);
                if (spanConsumed > 0)
                {
                    result.Append(span);
                    i += spanConsumed;
                    continue;
                }
            }

            if (char.IsWhiteSpace(c))
            {
                result.Append(c);
                i++;
                continue;
            }

            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            result.Append(RenderProseToken(line.Substring(start, i - start)));
        }

        return result.ToString();
    }

    /// <summary>
    /// Consumes a markdown link/image body starting at <paramref name="openBracket"/> (the
    /// <c>[</c>), returning 0 when the brackets/parens never resolve so the caller falls back to
    /// treating the character as plain text.
    /// </summary>
    private static int TryConsumeMarkdownLink(string line, int openBracket, out string consumed)
    {
        consumed = string.Empty;
        var closeBracket = line.IndexOf(']', openBracket + 1);
        if (closeBracket < 0 || closeBracket + 1 >= line.Length || line[closeBracket + 1] != '(')
        {
            return 0;
        }

        var depth = 1;
        var k = closeBracket + 2;
        while (k < line.Length && depth > 0)
        {
            if (line[k] == '(')
            {
                depth++;
            }
            else if (line[k] == ')')
            {
                depth--;
            }

            k++;
        }

        if (depth != 0)
        {
            return 0;
        }

        consumed = line.Substring(openBracket, k - openBracket);
        return k - openBracket;
    }

    private static int TryConsumeAutolink(string line, int openAngle, out string consumed)
    {
        consumed = string.Empty;
        var close = line.IndexOf('>', openAngle + 1);
        if (close < 0)
        {
            return 0;
        }

        var inner = line.Substring(openAngle + 1, close - openAngle - 1);
        if (!inner.StartsWith("http://", StringComparison.Ordinal) && !inner.StartsWith("https://", StringComparison.Ordinal))
        {
            return 0;
        }

        consumed = line.Substring(openAngle, close - openAngle + 1);
        return close - openAngle + 1;
    }

    private static bool IsUrlStart(string line, int i) =>
        (line.Length - i >= 8 && string.CompareOrdinal(line, i, "https://", 0, 8) == 0) ||
        (line.Length - i >= 7 && string.CompareOrdinal(line, i, "http://", 0, 7) == 0);

    private static int ConsumeUrlLength(string line, int start)
    {
        var i = start;
        while (i < line.Length && !char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        return i - start;
    }

    private static int TryConsumeCodeSpan(string line, int backtick, out string rendered)
    {
        var close = line.IndexOf('`', backtick + 1);
        if (close < 0)
        {
            rendered = string.Empty;
            return 0;
        }

        var content = line.Substring(backtick + 1, close - backtick - 1);
        var consumed = close - backtick + 1;

        rendered = content.Length > 0 && !ContainsWhitespace(content) && HasRecognizedExtension(content)
            ? "[`" + content + "`](" + BuildHref(content, null) + ")"
            : line.Substring(backtick, consumed);

        return consumed;
    }

    private static string RenderProseToken(string rawToken)
    {
        var coreLength = TrimTrailingPunctuation(rawToken);
        if (coreLength == 0)
        {
            return rawToken;
        }

        var core = rawToken.Substring(0, coreLength);
        var tail = rawToken.Substring(coreLength);

        var (pathPart, suffix, line) = ExtractLocationSuffix(core);
        var hasSeparator = ContainsSeparator(pathPart);
        var hasExtension = HasRecognizedExtension(pathPart);

        if (!hasExtension || (!hasSeparator && suffix.Length == 0))
        {
            return rawToken;
        }

        return "[" + EscapeBackslashesForDisplay(pathPart) + suffix + "](" + BuildHref(pathPart, line) + ")" + tail;
    }

    private static int TrimTrailingPunctuation(string token)
    {
        var end = token.Length;
        while (end > 0 && IsSentencePunctuation(token[end - 1]))
        {
            end--;
        }

        return end;
    }

    private static bool IsSentencePunctuation(char c) =>
        c == '.' || c == ',' || c == ';' || c == ':' || c == '!' || c == '?';

    /// <summary>
    /// Splits a trailing <c>:line</c>, <c>:line:col</c>, or <c>(line,col)</c> location suffix off
    /// <paramref name="core"/>. The column, when present, is only used to validate the suffix
    /// shape; callers never need it back.
    /// </summary>
    private static (string PathPart, string Suffix, int? Line) ExtractLocationSuffix(string core)
    {
        if (core.Length > 0 && core[core.Length - 1] == ')')
        {
            var openParen = core.LastIndexOf('(');
            if (openParen > 0)
            {
                var inner = core.Substring(openParen + 1, core.Length - openParen - 2);
                var comma = inner.IndexOf(',');
                if (comma > 0 && comma < inner.Length - 1)
                {
                    var row = inner.Substring(0, comma);
                    var column = inner.Substring(comma + 1);
                    if (IsDigitsOnly(row) && IsDigitsOnly(column))
                    {
                        return (core.Substring(0, openParen), core.Substring(openParen), ParsePositiveLine(row));
                    }
                }
            }
        }

        var lastColon = core.LastIndexOf(':');
        if (lastColon > 0 && lastColon < core.Length - 1)
        {
            var afterLast = core.Substring(lastColon + 1);
            if (IsDigitsOnly(afterLast))
            {
                var priorColon = core.LastIndexOf(':', lastColon - 1);
                if (priorColon > 0)
                {
                    var between = core.Substring(priorColon + 1, lastColon - priorColon - 1);
                    if (IsDigitsOnly(between))
                    {
                        return (core.Substring(0, priorColon), core.Substring(priorColon), ParsePositiveLine(between));
                    }
                }

                return (core.Substring(0, lastColon), core.Substring(lastColon), ParsePositiveLine(afterLast));
            }
        }

        return (core, string.Empty, null);
    }

    private static bool ContainsSeparator(string path)
    {
        foreach (var c in path)
        {
            if (c == '/' || c == '\\')
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDigitsOnly(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static int? ParsePositiveLine(string digits)
    {
        if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// True when the final path segment of <paramref name="path"/> has a <see cref="RecognizedExtensions"/>
    /// extension. A dot that belongs to an earlier segment (<c>src.old/Foo</c>) or that has no name
    /// before it in the final segment (<c>.gitignore</c>) does not count.
    /// </summary>
    private static bool HasRecognizedExtension(string path)
    {
        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1)
        {
            return false;
        }

        var lastSeparator = LastSeparatorIndex(path);
        if (dot < lastSeparator || dot == lastSeparator + 1)
        {
            return false;
        }

        return RecognizedExtensions.Contains(path.Substring(dot + 1));
    }

    private static int LastSeparatorIndex(string path)
    {
        var result = -1;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '/' || path[i] == '\\')
            {
                result = i;
            }
        }

        return result;
    }

    private static string EscapeBackslashesForDisplay(string path)
    {
        if (path.IndexOf('\\') < 0)
        {
            return path;
        }

        var builder = new StringBuilder(path.Length + 4);
        foreach (var c in path)
        {
            if (c == '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string BuildHref(string path, int? line)
    {
        var href = LinkPrefix + "path=" + Uri.EscapeDataString(path);
        return line.HasValue ? href + "&line=" + line.Value.ToString(CultureInfo.InvariantCulture) : href;
    }
}
