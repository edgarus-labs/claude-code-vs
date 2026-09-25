using System;
using System.Diagnostics;
using System.Text;
using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ChatFileReferenceTests
{
    private static string Link(string path) => ChatFileReference.LinkPrefix + "path=" + path;

    private static string Link(string path, int line) => ChatFileReference.LinkPrefix + "path=" + path + "&line=" + line;

    private static string Repeat(string unit, int totalLength)
    {
        var builder = new StringBuilder(totalLength + unit.Length);
        while (builder.Length < totalLength)
        {
            builder.Append(unit);
        }

        return builder.ToString();
    }

    private static TimeSpan FastestScan(string markdown)
    {
        var fastest = TimeSpan.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            ChatFileReference.LinkifyFileReferences(markdown);
            stopwatch.Stop();
            if (stopwatch.Elapsed < fastest)
            {
                fastest = stopwatch.Elapsed;
            }
        }

        return fastest;
    }

    // The reader sees only the file name; the full path the agent wrote rides in the href and is
    // used for nothing but navigation.
    [Fact]
    public void Linkify_PathInProse_ShowsTheFileNameAndLinksTheFullPath() =>
        Assert.Equal(
            "See [Foo.cs](" + Link("src%2FFoo.cs") + ") for details.",
            ChatFileReference.LinkifyFileReferences("See src/Foo.cs for details."));

    // The overwhelmingly common shape in agent answers: the path is already marked as a literal.
    // The link has to wrap the code span rather than replace it, or the reference stops looking
    // like code and the surrounding prose reflows.
    [Fact]
    public void Linkify_PathInCodeSpan_KeepsTheCodeSpanInsideTheLink() =>
        Assert.Equal(
            "See [`Foo.cs`](" + Link("src%2FFoo.cs") + ").",
            ChatFileReference.LinkifyFileReferences("See `src/Foo.cs`."));

    // An absolute path shows the same way, and the line the agent gave stays next to the name:
    // it is where the click puts the caret.
    [Fact]
    public void Linkify_AbsolutePathWithLineInCodeSpan_ShowsTheFileNameAndTheLine() =>
        Assert.Equal(
            "At [`Program.cs:12`](" + Link("C%3A%5Crepo%5Csrc%5CProgram.cs", 12) + ").",
            ChatFileReference.LinkifyFileReferences(@"At `C:\repo\src\Program.cs:12`."));

    // Agents often emphasise the reference itself - "1. **`src\Acp\Factory.cs`** — the factory".
    // The emphasis delimiters glued to the code span must not hide it: the link goes inside the
    // emphasis, so the reference renders both bold and clickable.
    [Theory]
    [InlineData(@"**`src\Acp\Factory.cs`**", "**[`Factory.cs`](", "src%5CAcp%5CFactory.cs", ")**")]
    [InlineData("__`src/Foo.cs`__", "__[`Foo.cs`](", "src%2FFoo.cs", ")__")]
    [InlineData("*`Foo.cs`*", "*[`Foo.cs`](", "Foo.cs", ")*")]
    [InlineData("***`Foo.cs`***.", "***[`Foo.cs`](", "Foo.cs", ")***.")]
    public void Linkify_EmphasisedCodeSpan_LinksInsideTheEmphasis(string token, string before, string encodedPath, string after) =>
        Assert.Equal(
            "1. " + before + Link(encodedPath) + after + " — the factory",
            ChatFileReference.LinkifyFileReferences("1. " + token + " — the factory"));

    // The same for a path written as emphasised prose rather than a code span.
    [Theory]
    [InlineData(@"**src\Foo.cs**", "**[Foo.cs](", "src%5CFoo.cs", ")**")]
    [InlineData("_Program.cs:42_,", "_[Program.cs:42](", "Program.cs&line=42", ")_,")]
    public void Linkify_EmphasisedPathInProse_LinksInsideTheEmphasis(string token, string before, string encodedPathAndLine, string after) =>
        Assert.Equal(
            "See " + before + ChatFileReference.LinkPrefix + "path=" + encodedPathAndLine + after,
            ChatFileReference.LinkifyFileReferences("See " + token));

    // Underscores are file-name characters too: only a run wrapping the whole token on both sides
    // is emphasis. A leading-only run belongs to the name and must stay part of it.
    [Theory]
    [InlineData("pkg/__init__.py", "__init__.py", "pkg%2F__init__.py")]
    [InlineData("__init__.py:3", "__init__.py:3", "__init__.py&line=3")]
    public void Linkify_PathWithUnderscoresInTheName_KeepsThemInThePath(string token, string shown, string encodedPathAndLine) =>
        Assert.Equal(
            "[" + shown + "](" + ChatFileReference.LinkPrefix + "path=" + encodedPathAndLine + ")",
            ChatFileReference.LinkifyFileReferences(token));

    // The shape agent answers actually use most: a backticked path carrying a location suffix
    // ("Found it: `src\ClaudeCode.Vsix\EditorCaret.cs:1`"). The suffix has to come off before the
    // extension is judged, or the extension reads as "cs:1", matches nothing, and the single most
    // common real reference silently stays plain text.
    [Theory]
    [InlineData("`src/Foo.cs:12`", "[`Foo.cs:12`]", "src%2FFoo.cs", 12)]
    [InlineData("`src/Foo.cs:12:5`", "[`Foo.cs:12:5`]", "src%2FFoo.cs", 12)]
    [InlineData("`src/Foo.cs(12,5)`", "[`Foo.cs(12,5)`]", "src%2FFoo.cs", 12)]
    [InlineData(@"`src\ClaudeCode.Vsix\EditorCaret.cs:1`", "[`EditorCaret.cs:1`]", "src%5CClaudeCode.Vsix%5CEditorCaret.cs", 1)]
    public void Linkify_CodeSpanWithLocationSuffix_LinksThePathAndKeepsTheLine(
        string span, string expectedText, string encodedPath, int line) =>
        Assert.Equal(
            "Found it: " + expectedText + "(" + Link(encodedPath, line) + ")",
            ChatFileReference.LinkifyFileReferences("Found it: " + span));

    // A bare file name has no path separator, so only the backticks distinguish "the file README.md"
    // from ordinary prose. Inside a code span the author already said it is a literal.
    [Fact]
    public void Linkify_BareFileNameInCodeSpan_BecomesALink() =>
        Assert.Equal(
            "Open [`README.md`](" + Link("README.md") + ").",
            ChatFileReference.LinkifyFileReferences("Open `README.md`."));

    // ...and outside one it is not: "Node.js", "asp.net" and "v1.2.zip-style" words are far more
    // common in prose than a bare unqualified file name, and a wrong link is worse than no link.
    [Theory]
    [InlineData("We use Node.js here.")]
    [InlineData("Runs on asp.net today.")]
    public void Linkify_BareFileNameInProse_IsLeftAlone(string markdown) =>
        Assert.Equal(markdown, ChatFileReference.LinkifyFileReferences(markdown));

    // A line suffix is unambiguous on its own, so it qualifies a bare file name in prose too.
    [Fact]
    public void Linkify_BareFileNameWithLineInProse_BecomesALink() =>
        Assert.Equal(
            "Fails at [Program.cs:42](" + Link("Program.cs", 42) + ").",
            ChatFileReference.LinkifyFileReferences("Fails at Program.cs:42."));

    [Theory]
    // path:line, path:line:column, and the VS/MSBuild path(line,column) diagnostic form.
    [InlineData("src/Foo.cs:12", "Foo.cs:12", "src%2FFoo.cs", 12)]
    [InlineData("src/Foo.cs:12:5", "Foo.cs:12:5", "src%2FFoo.cs", 12)]
    [InlineData("src/Foo.cs(12,5)", "Foo.cs(12,5)", "src%2FFoo.cs", 12)]
    public void Linkify_CommonLocationSuffixes_YieldThePathAndTheLine(string token, string shown, string encoded, int line) =>
        Assert.Equal(
            "[" + shown + "](" + Link(encoded, line) + ")",
            ChatFileReference.LinkifyFileReferences(token));

    // A Windows separator splits the name off like a forward slash does; the path it belongs to
    // survives only in the href.
    [Fact]
    public void Linkify_WindowsPathInProse_ShowsTheFileNameAndLinksTheFullPath() =>
        Assert.Equal(
            "Edit [Foo.cs](" + Link("src%5CFoo.cs") + ").",
            ChatFileReference.LinkifyFileReferences(@"Edit src\Foo.cs."));

    // Square brackets are link-text delimiters, so a path carrying one breaks the emitted link:
    // "docs/a].md" ends the text at the "]" and markdown-it renders the rest - including the raw
    // "/__claudecode/open?..." destination - as visible text in the answer.
    [Theory]
    [InlineData("docs/a].md", @"a\].md", "docs%2Fa%5D.md")]
    [InlineData("docs/[a].md", @"\[a\].md", "docs%2F%5Ba%5D.md")]
    public void Linkify_PathWithBracketsInProse_EscapesThemInTheLinkText(
        string path, string expectedText, string encodedPath) =>
        Assert.Equal(
            "Open [" + expectedText + "](" + Link(encodedPath) + ").",
            ChatFileReference.LinkifyFileReferences("Open " + path + "."));

    // Uri.EscapeDataString is not stable across target frameworks: on net472/net48 - what the
    // extension actually ships on - it follows RFC 2396 and leaves the marks !*'() unescaped,
    // while the net10.0 test host follows RFC 3986 and escapes them. The parentheses are what
    // corrupts output: markdown-it ends a destination at the first unbalanced ")", leaking the
    // remainder of the href as text. The emitted markdown must not depend on the framework.
    [Fact]
    public void Linkify_PathWithRfc2396Marks_PercentEncodesThemAndStillRoundTrips()
    {
        var markdown = ChatFileReference.LinkifyFileReferences("Open src/a(1)!'*.cs:7.");

        Assert.Equal("Open [a(1)!'*.cs:7](" + Link("src%2Fa%281%29%21%27%2A.cs", 7) + ").", markdown);

        var start = markdown.IndexOf(ChatFileReference.LinkPrefix, StringComparison.Ordinal);
        var href = markdown.Substring(start, markdown.Length - start - 2);
        Assert.True(ChatFileReference.TryParseLink(href, out var path, out var line));
        Assert.Equal("src/a(1)!'*.cs", path);
        Assert.Equal(7, line);
    }

    // Sentence punctuation is not part of the reference; swallowing it produces a path that can
    // never resolve.
    [Fact]
    public void Linkify_TrailingSentencePunctuation_StaysOutsideTheLink() =>
        Assert.Equal(
            "Look at [Foo.cs](" + Link("src%2FFoo.cs") + "), then stop.",
            ChatFileReference.LinkifyFileReferences("Look at src/Foo.cs, then stop."));

    // Rewriting inside a fence would show the reader "[src/Foo.cs](/__claudecode/open?...)" as code.
    [Fact]
    public void Linkify_FencedCodeBlock_IsLeftAlone()
    {
        const string markdown = "Before src/A.cs\n```\nusing src/Foo.cs;\n```\nafter";
        Assert.Equal(
            "Before [A.cs](" + Link("src%2FA.cs") + ")\n```\nusing src/Foo.cs;\n```\nafter",
            ChatFileReference.LinkifyFileReferences(markdown));
    }

    // markdown-it fences with "~~~" as well as "```", and accepts up to three leading spaces -
    // exactly what an agent emits for a code block nested in a bullet. Miss either shape and every
    // line of the block is rewritten, so the reader sees a raw href inside code.
    [Theory]
    [InlineData("~~~", "")]
    [InlineData("```", "   ")]
    [InlineData("~~~", "   ")]
    public void Linkify_FenceMarkerVariants_LeaveTheBlockAloneAndStillClose(string fence, string indent) =>
        Assert.Equal(
            indent + fence + "\nusing src/Foo.cs;\n" + indent + fence + "\nsee [A.cs](" + Link("src%2FA.cs") + ")",
            ChatFileReference.LinkifyFileReferences(
                indent + fence + "\nusing src/Foo.cs;\n" + indent + fence + "\nsee src/A.cs"));

    // A stray "```" inside a "~~~" block must not close it: the fence state would invert and every
    // line after it in the message would be classified backwards.
    [Fact]
    public void Linkify_ForeignFenceMarkerInsideAFence_DoesNotCloseIt() =>
        Assert.Equal(
            "~~~\n```\nusing src/Foo.cs;\n~~~\nsee [A.cs](" + Link("src%2FA.cs") + ")",
            ChatFileReference.LinkifyFileReferences("~~~\n```\nusing src/Foo.cs;\n~~~\nsee src/A.cs"));

    // Four leading spaces is an indented code block to markdown-it; the same mangling applies.
    [Fact]
    public void Linkify_IndentedCodeBlock_IsLeftAlone() =>
        Assert.Equal(
            "text\n\n    open src/Foo.cs\n",
            ChatFileReference.LinkifyFileReferences("text\n\n    open src/Foo.cs\n"));

    // Nesting a link inside a link's text or destination produces broken markup.
    [Theory]
    [InlineData("[src/Foo.cs](https://example.com/x)")]
    [InlineData("![diagram](docs/diagram.png)")]
    public void Linkify_ExistingMarkdownLink_IsLeftAlone(string markdown) =>
        Assert.Equal(markdown, ChatFileReference.LinkifyFileReferences(markdown));

    // A destination too long for the scan bound has to be copied through verbatim rather than
    // handed back to the prose path: prose would rewrite the path-shaped tail of the destination
    // and nest a link inside a link, which is visible the moment the link also carries a title.
    // The reference after it still linkifies, so the scan resumes in the right place.
    [Fact]
    public void Linkify_LinkWithAnOverlongDestination_IsCopiedThroughVerbatim()
    {
        var destination = new string('a', 600) + "/b.md";
        Assert.Equal(
            "[text](" + destination + " \"T\") and [A.cs](" + Link("src%2FA.cs") + ")",
            ChatFileReference.LinkifyFileReferences("[text](" + destination + " \"T\") and src/A.cs"));
    }

    // linkify already turns these into anchors the host opens in a browser; claiming them as
    // workspace files would break real navigation.
    [Theory]
    [InlineData("See https://example.com/docs/guide.html for more.")]
    [InlineData("See <https://example.com/docs/guide.html> for more.")]
    public void Linkify_Url_IsLeftAlone(string markdown) =>
        Assert.Equal(markdown, ChatFileReference.LinkifyFileReferences(markdown));

    // A code span is used for far more than paths; only a whole-span path may be claimed.
    [Theory]
    [InlineData("Run `dotnet build` now.")]
    [InlineData("Use `var x = a.b;` here.")]
    [InlineData("Version `1.0.0` shipped.")]
    [InlineData("Tag `v1.2` shipped.")]
    [InlineData("Call `list.Add` twice.")]
    public void Linkify_CodeSpanThatIsNotAPath_IsLeftAlone(string markdown) =>
        Assert.Equal(markdown, ChatFileReference.LinkifyFileReferences(markdown));

    // The code-span branch has to refuse what the prose branch refuses, or the two diverge: a URL's
    // last segment ("guide.html") is not a workspace file, and neither a CLI flag nor a config
    // assignment is a path - linking them hands the host something no editor can open.
    [Theory]
    [InlineData("See `https://example.com/docs/guide.html` for more.")]
    [InlineData("See `http://example.com/docs/guide.html` for more.")]
    [InlineData("Pass `--out=foo.json` to it.")]
    [InlineData("Set `key=value.yml` there.")]
    public void Linkify_CodeSpanThatIsAUrlOrAnAssignment_IsLeftAlone(string markdown) =>
        Assert.Equal(markdown, ChatFileReference.LinkifyFileReferences(markdown));

    // The allow-list is the whole gate on "looks like a file", so a missing entry is a silent
    // no-link: this repo's own solution file is ClaudeCodeVS.slnx.
    [Theory]
    [InlineData("ClaudeCodeVS.slnx")]
    [InlineData("Native.vcxproj")]
    [InlineData("rows.csv")]
    [InlineData("build.log")]
    [InlineData("chat.proto")]
    public void Linkify_RecognizedExtension_InACodeSpanBecomesALink(string path) =>
        Assert.Equal(
            "Open [`" + path + "`](" + Link(path) + ").",
            ChatFileReference.LinkifyFileReferences("Open `" + path + "`."));

    // Every debounced transcript repaint re-runs this scan on the WPF UI thread over a message
    // capped at MaxMarkdownLength. A "[" or "<" that never closes must not make the lookahead for
    // its closing delimiter rescan the rest of the line from every token: on a capped line that is
    // ~10^10 character comparisons - the same class of defect as the 13 s quadratic diff-header
    // regex this repo already fixed once (net472's scalar IndexOf makes it seconds in production;
    // the net10.0 test host's vectorized IndexOf only hides the factor, it does not remove it).
    // The bound is relative to plain text of the same size so it measures the growth rate rather
    // than the speed of whatever machine runs it.
    [Theory]
    [InlineData("[ ")]
    [InlineData("< ")]
    public void Linkify_UnclosedDelimiterAtEveryToken_CostsAboutTheSameAsPlainText(string unit)
    {
        var unclosed = Repeat(unit, MarkdownSafetyLimits.MaxMarkdownLength);
        var plainCost = FastestScan(Repeat("a ", MarkdownSafetyLimits.MaxMarkdownLength));
        var unclosedCost = FastestScan(unclosed);

        Assert.Equal(unclosed, ChatFileReference.LinkifyFileReferences(unclosed));
        Assert.True(
            unclosedCost.Ticks < plainCost.Ticks * 4,
            "plain text: " + plainCost + ", unclosed \"" + unit.Trim() + "\": " + unclosedCost);
    }

    // The other half of the same defect: an unbalanced "(" in a link destination made the scan for
    // the matching ")" run to the end of the line from every "[" on it. Measured on this scanner
    // before the destination lookahead was bounded: 14.2 s for "[]( " and 11.0 s for "[](( "
    // repeated to the message cap, against 9 ms for plain text. A bounded lookahead cannot be as
    // cheap as plain text, only a small constant multiple of it - the nested row matters because a
    // fix that only bails when the line has no ")" at all still leaves "[](( " quadratic.
    [Theory]
    [InlineData("[]( ")]
    [InlineData("[](( ")]
    public void Linkify_UnclosedLinkDestinationAtEveryToken_StaysBounded(string unit)
    {
        var unclosed = Repeat(unit, MarkdownSafetyLimits.MaxMarkdownLength);
        var plainCost = FastestScan(Repeat("a ", MarkdownSafetyLimits.MaxMarkdownLength));
        var unclosedCost = FastestScan(unclosed);

        Assert.Equal(unclosed, ChatFileReference.LinkifyFileReferences(unclosed));
        Assert.True(
            unclosedCost.Ticks < plainCost.Ticks * 20,
            "plain text: " + plainCost + ", unclosed \"" + unit.Trim() + "\": " + unclosedCost);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("nothing to see", "nothing to see")]
    public void Linkify_NoReference_ReturnsTheTextUnchanged(string? markdown, string expected) =>
        Assert.Equal(expected, ChatFileReference.LinkifyFileReferences(markdown));

    [Fact]
    public void TryParseLink_GeneratedLink_RoundTripsThePathAndLine()
    {
        var markdown = ChatFileReference.LinkifyFileReferences(@"Edit src\Foo.cs:7");
        var start = markdown.IndexOf(ChatFileReference.LinkPrefix, System.StringComparison.Ordinal);
        var href = markdown.Substring(start, markdown.Length - start - 1);

        Assert.True(ChatFileReference.TryParseLink(href, out var path, out var line));
        Assert.Equal(@"src\Foo.cs", path);
        Assert.Equal(7, line);
    }

    [Fact]
    public void TryParseLink_NoLine_ReportsNoLine()
    {
        Assert.True(ChatFileReference.TryParseLink(Link("src%2FFoo.cs"), out var path, out var line));
        Assert.Equal("src/Foo.cs", path);
        Assert.Null(line);
    }

    // The href arrives from the transcript page, which renders untrusted agent markdown: everything
    // that is not this renderer's own link must fail closed rather than reach the file system.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/")]
    [InlineData("/__claudecode/open?line=3")]
    [InlineData("/__claudecode/open?path=")]
    [InlineData("/__claudecode/openx?path=a.cs")]
    [InlineData("__claudecode/open?path=a.cs")]
    public void TryParseLink_AnythingButThisRenderersLink_IsRejected(string? href)
    {
        Assert.False(ChatFileReference.TryParseLink(href, out var path, out var line));
        Assert.Equal(string.Empty, path);
        Assert.Null(line);
    }

    // A non-numeric or absurd line must not fail the whole open - the file is still the point.
    [Theory]
    [InlineData(ChatFileReference.LinkPrefix + "path=a.cs&line=abc")]
    [InlineData(ChatFileReference.LinkPrefix + "path=a.cs&line=0")]
    [InlineData(ChatFileReference.LinkPrefix + "path=a.cs&line=-4")]
    [InlineData(ChatFileReference.LinkPrefix + "path=a.cs&line=99999999999999999999")]
    public void TryParseLink_UnusableLine_StillOpensTheFile(string href)
    {
        Assert.True(ChatFileReference.TryParseLink(href, out var path, out var line));
        Assert.Equal("a.cs", path);
        Assert.Null(line);
    }
}
