using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Turns file-like tokens in assistant-authored Markdown into clickable transcript links, and parses
/// those links back into a path/line pair. Runs in time linear in the message length. Fenced/indented
/// code blocks, existing markdown links and images, and autolinked/bare URLs are left untouched.
/// </summary>
public static class ChatFileReference
{
    /// <summary>Href prefix of a transcript file-reference link.</summary>
    public const string LinkPrefix = "/__claudecode/open?";

    /// <summary>
    /// The max link destination length.
    /// </summary>
    private const int _maxLinkDestinationLength = 512;

    private static readonly HashSet<string> _recognizedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cs", "csx", "csproj", "vbproj", "fsproj", "vcxproj", "sln", "slnx", "vb", "fs", "fsx",
        "ts", "tsx", "js", "jsx", "mjs", "cjs",
        "json", "jsonc", "xml", "xaml", "resx", "config", "props", "targets", "proto",
        "yml", "yaml", "toml", "ini",
        "md", "mdx", "txt", "csv", "log",
        "html", "htm", "css", "scss", "less",
        "py", "rb", "go", "rs", "java", "kt", "c", "h", "cpp", "cc", "hpp", "cxx",
        "sh", "ps1", "psm1", "bat", "cmd", "sql",
        "cshtml", "razor", "vue", "svelte", "php",
    };

    /// <summary>
    /// Rewrites file references into transcript links. Scans line by line so fenced (<c>```</c> or
    /// <c>~~~</c>) and 4-space/tab-indented code blocks can be skipped outright; within a remaining
    /// line, existing markdown links/images (<c>[...](...)</c>, <c>![...](...)</c>) and autolinked
    /// or bare URLs are copied through untouched, a backtick code span becomes a link only when its
    /// entire content is path-shaped, and a plain-prose word becomes a link when it carries a
    /// recognized file extension together with either a path separator or a <c>:line</c>,
    /// <c>:line:col</c>, or <c>(line,col)</c> location suffix. Trailing sentence punctuation and
    /// <c>*</c>/<c>_</c> emphasis wrapping the reference (<c>**`src\Foo.cs`**</c>) are kept outside
    /// the link. The link shows only the file name and the location suffix the agent wrote
    /// (<c>Foo.cs:12</c>); the full path goes into the href, which is used for navigation only.
    /// </summary>
    public static string LinkifyFileReferences(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var lines = markdown!.Split('\n');
        var openFenceMarker = '\0';

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (TryGetFenceMarker(line, out var marker))
            {
                if (openFenceMarker == '\0')
                {
                    openFenceMarker = marker;
                }
                else if (openFenceMarker == marker)
                {
                    openFenceMarker = '\0';
                }

                continue;
            }

            if (openFenceMarker != '\0' || IsIndentedCodeLine(line))
            {
                continue;
            }

            lines[i] = LinkifyLine(line);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Parses a transcript file-reference link back into a path and optional line. Returns
    /// <see langword="false"/> for anything that is not a <see cref="LinkPrefix"/> link with a
    /// non-empty <c>path</c> query value. An unusable <c>line</c> value (missing, non-numeric,
    /// zero/negative, or too large to fit an <see cref="int"/>) yields the path without a line.
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

    private static bool TryGetFenceMarker(string line, out char marker)
    {
        marker = '\0';

        var start = 0;
        while (start < 3 && start < line.Length && line[start] == ' ')
        {
            start++;
        }

        if (line.Length - start < 3)
        {
            return false;
        }

        var candidate = line[start];
        if ((candidate != '`' && candidate != '~') || line[start + 1] != candidate || line[start + 2] != candidate)
        {
            return false;
        }

        marker = candidate;
        return true;
    }

    private static string LinkifyLine(string line)
    {
        var result = new StringBuilder(line.Length);

        var nextCloseBracket = line.IndexOf(']');
        var nextCloseAngle = line.IndexOf('>');
        var i = 0;

        while (i < line.Length)
        {
            var c = line[i];

            if (c == '!' && i + 1 < line.Length && line[i + 1] == '[')
            {
                var imageConsumed = TryConsumeMarkdownLink(line, i + 1, CloseBracketAfter(i + 1), out var image);
                if (imageConsumed > 0)
                {
                    result.Append('!').Append(image);
                    i += 1 + imageConsumed;
                    continue;
                }
            }

            if (c == '[')
            {
                var linkConsumed = TryConsumeMarkdownLink(line, i, CloseBracketAfter(i), out var link);
                if (linkConsumed > 0)
                {
                    result.Append(link);
                    i += linkConsumed;
                    continue;
                }
            }

            if (c == '<')
            {
                var autolinkConsumed = TryConsumeAutolink(line, i, CloseAngleAfter(i), out var autolink);
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

            if (c == '*' || c == '_')
            {
                var run = DelimiterRunLength(line, i);
                if (i + run < line.Length && line[i + run] == '`')
                {
                    result.Append(line, i, run);
                    i += run;
                    continue;
                }
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

        int CloseBracketAfter(int openBracket)
        {
            if (nextCloseBracket >= 0 && nextCloseBracket <= openBracket)
            {
                nextCloseBracket = line.IndexOf(']', openBracket + 1);
            }

            return nextCloseBracket;
        }

        int CloseAngleAfter(int openAngle)
        {
            if (nextCloseAngle >= 0 && nextCloseAngle <= openAngle)
            {
                nextCloseAngle = line.IndexOf('>', openAngle + 1);
            }

            return nextCloseAngle;
        }
    }

    private static int TryConsumeMarkdownLink(string line, int openBracket, int closeBracket, out string consumed)
    {
        consumed = string.Empty;
        if (closeBracket < 0 || closeBracket + 1 >= line.Length || line[closeBracket + 1] != '(')
        {
            return 0;
        }

        var depth = 1;
        var k = closeBracket + 2;
        var limit = Math.Min(line.Length, k + _maxLinkDestinationLength);
        while (k < limit && depth > 0)
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
            if (limit == line.Length)
            {
                return 0;
            }

            var runEnd = openBracket;
            while (runEnd < line.Length && !char.IsWhiteSpace(line[runEnd]))
            {
                runEnd++;
            }

            consumed = line.Substring(openBracket, runEnd - openBracket);
            return runEnd - openBracket;
        }

        consumed = line.Substring(openBracket, k - openBracket);
        return k - openBracket;
    }

    private static int TryConsumeAutolink(string line, int openAngle, int closeAngle, out string consumed)
    {
        consumed = string.Empty;
        if (closeAngle < 0)
        {
            return 0;
        }

        var inner = line.Substring(openAngle + 1, closeAngle - openAngle - 1);
        if (!inner.StartsWith("http://", StringComparison.Ordinal) && !inner.StartsWith("https://", StringComparison.Ordinal))
        {
            return 0;
        }

        consumed = line.Substring(openAngle, closeAngle - openAngle + 1);
        return closeAngle - openAngle + 1;
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

        var (pathPart, suffix, lineNumber) = ExtractLocationSuffix(content);

        var isPathShaped = pathPart.Length > 0
            && !ContainsWhitespace(content)
            && !IsUrlStart(content, 0)
            && content.IndexOf('=') < 0
            && HasRecognizedExtension(pathPart);

        rendered = isPathShaped
            ? "[`" + FileName(pathPart) + suffix + "`](" + BuildHref(pathPart, lineNumber) + ")"
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

        var run = DelimiterRunLength(core, 0);
        if (run > 0 && core.Length > 2 * run && string.CompareOrdinal(core, core.Length - run, core, 0, run) == 0)
        {
            var delimiters = core.Substring(0, run);
            var inner = TryRenderPath(core.Substring(run, core.Length - 2 * run));
            return inner is null ? rawToken : delimiters + inner + delimiters + tail;
        }

        var rendered = TryRenderPath(core);
        return rendered is null ? rawToken : rendered + tail;
    }

    private static string? TryRenderPath(string core)
    {
        var (pathPart, suffix, line) = ExtractLocationSuffix(core);
        var hasSeparator = ContainsSeparator(pathPart);
        var hasExtension = HasRecognizedExtension(pathPart);

        if (!hasExtension || (!hasSeparator && suffix.Length == 0))
        {
            return null;
        }

        return "[" + EscapeLinkText(FileName(pathPart)) + suffix + "](" + BuildHref(pathPart, line) + ")";
    }

    private static int DelimiterRunLength(string text, int index)
    {
        var delimiter = text[index];
        if (delimiter != '*' && delimiter != '_')
        {
            return 0;
        }

        var end = index;
        while (end < text.Length && text[end] == delimiter)
        {
            end++;
        }

        return end - index;
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

        return _recognizedExtensions.Contains(path.Substring(dot + 1));
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

    private static string FileName(string path) => path.Substring(LastSeparatorIndex(path) + 1);

    private static string EscapeLinkText(string fileName)
    {
        var needsEscaping = false;
        foreach (var c in fileName)
        {
            if (IsLinkTextEscape(c))
            {
                needsEscaping = true;
                break;
            }
        }

        if (!needsEscaping)
        {
            return fileName;
        }

        var builder = new StringBuilder(fileName.Length + 4);
        foreach (var c in fileName)
        {
            if (IsLinkTextEscape(c))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsLinkTextEscape(char c) => c == '[' || c == ']' || c == '_';

    private static string BuildHref(string path, int? line)
    {
        var href = LinkPrefix + "path=" + EncodePathValue(path);
        return line.HasValue ? href + "&line=" + line.Value.ToString(CultureInfo.InvariantCulture) : href;
    }

    /// <summary>
    /// Encodes the given path string by applying URI escaping and percent‑encoding of RFC 2396 reserved characters.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The string result.</returns>
    private static string EncodePathValue(string path)
    {
        var escaped = Uri.EscapeDataString(path);

        var needsEncoding = false;
        foreach (var c in escaped)
        {
            if (IsRfc2396Mark(c))
            {
                needsEncoding = true;
                break;
            }
        }

        if (!needsEncoding)
        {
            return escaped;
        }

        var builder = new StringBuilder(escaped.Length + 8);
        foreach (var c in escaped)
        {
            switch (c)
            {
                case '!':
                    builder.Append("%21");
                    break;

                case '*':
                    builder.Append("%2A");
                    break;

                case '\'':
                    builder.Append("%27");
                    break;

                case '(':
                    builder.Append("%28");
                    break;

                case ')':
                    builder.Append("%29");
                    break;

                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool IsRfc2396Mark(char c) => c == '!' || c == '*' || c == '\'' || c == '(' || c == ')';
}
