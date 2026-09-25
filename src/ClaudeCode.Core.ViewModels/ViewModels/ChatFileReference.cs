using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Turns file-like tokens in assistant-authored Markdown into clickable links the WebView2
/// transcript can post back to the host, and parses those links back into a path/line pair. The
/// markdown is untrusted model output, so this never uses regular expressions for the scan - a
/// hand-rolled scanner whose scan index only ever moves forward, whose closing-delimiter lookahead
/// is cached in monotone cursors, and whose link-destination lookahead is length-bounded (the
/// caller already bounds input length via <see cref="MarkdownSafetyLimits.MaxMarkdownLength"/>)
/// keeps the cost linear in the message length and rules out any catastrophic-backtracking
/// exposure. Linearity is a hard requirement, not a nicety: this runs on the UI thread on every
/// debounced transcript repaint, so re-scanning the rest of a line once per token surfaces as a
/// frozen window on a capped-size message - measured at 14 s before those bounds existed.
/// Fenced/indented code blocks, existing markdown links and images, and autolinked/bare URLs are
/// all left untouched so the rewrite never nests inside content that already has its own
/// destination.
/// </summary>
public static class ChatFileReference
{
    /// <summary>Href prefix of a transcript file-reference link.</summary>
    public const string LinkPrefix = "/__claudecode/open?";

    /// <summary>
    /// How far past a <c>](</c> the destination scan looks for the matching <c>)</c>. A destination
    /// an agent writes is a path or a URL - a few hundred characters at the very most - whereas an
    /// unbalanced <c>(</c> makes an unbounded scan run to the end of the line from every <c>[</c> on
    /// it, which is quadratic: <c>"[]( "</c> repeated to the 200 000-char message cap measured 14 s
    /// in this scanner, the same frozen-UI defect class as the quadratic diff-header regex this
    /// repo already had to fix. When the bound cuts a scan short the run is copied through
    /// verbatim (see <see cref="TryConsumeMarkdownLink"/>), so a destination this long costs the
    /// reader nothing but an unlinked reference inside it.
    /// </summary>
    private const int MaxLinkDestinationLength = 512;

    /// <summary>
    /// Extensions that make a token "look like a file" for linkification. A fixed allow-list
    /// (rather than "any token with a dot") is what keeps version-ish or member-access-ish
    /// backtick content such as <c>1.0.0</c>, <c>v1.2</c>, or <c>list.Add</c> from being mistaken
    /// for a file, and what keeps a bare word like "Node.js" alone in prose from being linkified.
    /// </summary>
    private static readonly HashSet<string> RecognizedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>
    /// Reports the fence marker of a code-fence line: three or more <c>`</c> or <c>~</c> after up
    /// to three leading spaces, which is what markdown-it - the transcript's renderer - accepts.
    /// Both the tilde form and the 1-3 space indent occur constantly in agent answers (an indented
    /// fence is exactly what a code block nested in a bullet looks like), and an unrecognized fence
    /// means every line of that block gets rewritten, showing the reader a raw
    /// <c>/__claudecode/open?...</c> destination inside code. Reporting the marker rather than a
    /// bool is what lets a fence close only on the character that opened it: a stray <c>```</c>
    /// inside a <c>~~~</c> block would otherwise invert the state and mis-classify every remaining
    /// line of the message.
    /// </summary>
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

        // Monotone lookahead cursors for the delimiters that close a markdown link and an autolink.
        // A fresh IndexOf from every '[' or '<' is quadratic whenever the delimiter never closes -
        // a capped 200 000-char message of "[ " is ~10^10 character comparisons, on the UI thread,
        // on every repaint - and it is pure waste: the scan index below only ever moves forward, so
        // a cached hit stays the first one ahead of it until the scan passes it, and a miss (-1) is
        // final for the rest of the line. Each cursor therefore costs one pass over the line in
        // total, whatever the input shape.
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

            // Emphasis glued to a code span ("**`src\Foo.cs`**"): copy the delimiter run through so
            // the span itself is judged on the next pass and the link lands inside the emphasis.
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

    /// <summary>
    /// Consumes a markdown link/image body starting at <paramref name="openBracket"/> (the
    /// <c>[</c>), with <paramref name="closeBracket"/> supplied by the caller's monotone cursor for
    /// the next <c>]</c> after it, returning 0 when the brackets/parens never resolve within
    /// <see cref="MaxLinkDestinationLength"/> so the caller falls back to treating the character as
    /// plain text.
    /// </summary>
    private static int TryConsumeMarkdownLink(string line, int openBracket, int closeBracket, out string consumed)
    {
        consumed = string.Empty;
        if (closeBracket < 0 || closeBracket + 1 >= line.Length || line[closeBracket + 1] != '(')
        {
            return 0;
        }

        var depth = 1;
        var k = closeBracket + 2;
        var limit = Math.Min(line.Length, k + MaxLinkDestinationLength);
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
            // Nothing closed the destination. If the line itself ran out, fall back to plain text:
            // the prose path then reads the "[...](..." fragment as ordinary words, exactly as it
            // always has. If instead the bound above cut the scan short, the whole
            // whitespace-delimited run is copied through verbatim, because prose-scanning the tail
            // of a 512+ character destination would linkify a path-shaped fragment inside a link
            // and nest one link in another. Every character copied here is consumed, so the run
            // costs one pass and the line stays linear.
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

        // Split the location suffix off before judging the extension, exactly as the prose path
        // does. Without this "`src\Foo.cs:12`" - the shape agent answers overwhelmingly use - reads
        // as extension "cs:12", matches nothing, and silently stays plain text; and even a match
        // would hand the host a path with ":12" glued on that no editor can open. The visible code
        // span keeps only the file name and the suffix ("`Foo.cs:12`") - the path lives in the href -
        // and needs no escaping: a code span is already literal.
        var (pathPart, suffix, lineNumber) = ExtractLocationSuffix(content);

        // The two branches have to refuse the same things or they ship different bugs: prose never
        // sees a URL because the line scanner consumes it first, so without the same check here
        // "`https://example.com/docs/guide.html`" turns into an editor link to a file named
        // "guide.html". An "=" is the other giveaway that a span is not a reference but a CLI flag
        // or a setting ("--out=foo.json", "key=value.yml"), where the extension belongs to a value,
        // not to a file the host could open.
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

        // Emphasis wrapping the whole path ("**src\Foo.cs**") stays outside the link. Only a run
        // repeated at both ends counts: a leading-only run ("__init__.py:3") is part of the name.
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

    // Length of the run of the '*' or '_' emphasis delimiter starting at index (0 when none).
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

    // What a reference shows: the final segment of the path. The full path the agent wrote goes
    // into the href, where it is used for navigation only.
    private static string FileName(string path) => path.Substring(LastSeparatorIndex(path) + 1);

    /// <summary>
    /// Escapes the characters markdown reads specially inside link text. A square bracket delimits
    /// the link text itself: <c>a].md</c> emitted raw ends the text at the <c>]</c>, so markdown-it
    /// renders the rest, destination included, as visible text. The text is a file name, so it
    /// never holds a backslash - that is a path separator. The code-span branch needs none of this
    /// because a code span binds tighter than the brackets.
    /// </summary>
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

    private static bool IsLinkTextEscape(char c) => c == '[' || c == ']';

    private static string BuildHref(string path, int? line)
    {
        var href = LinkPrefix + "path=" + EncodePathValue(path);
        return line.HasValue ? href + "&line=" + line.Value.ToString(CultureInfo.InvariantCulture) : href;
    }

    /// <summary>
    /// Percent-encodes the path for the query string. <see cref="Uri.EscapeDataString"/> alone is
    /// not enough because it is not stable across target frameworks: on net472/net48 - what the
    /// extension actually ships on - it follows RFC 2396 and leaves the marks <c>!*'()</c>
    /// unescaped, while a modern .NET host (including the test host) follows RFC 3986 and escapes
    /// them. The parentheses are what corrupts output: markdown-it ends a link destination at the
    /// first unbalanced <c>)</c>, so a path such as <c>src/a(1).cs</c> would leak the rest of the
    /// href into the rendered answer - in production only, invisibly to the tests. Encoding the
    /// whole divergent set here makes the emitted markdown identical on every framework;
    /// <see cref="TryParseLink"/> reads it back with <see cref="Uri.UnescapeDataString"/>, which
    /// decodes all of them, and the raw <c>&amp;</c>/<c>=</c> split it does first is unaffected
    /// because neither character is a mark.
    /// </summary>
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
