using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ChatFileReferenceTests
{
    private static string Link(string path) => ChatFileReference.LinkPrefix + "path=" + path;

    private static string Link(string path, int line) => ChatFileReference.LinkPrefix + "path=" + path + "&line=" + line;

    [Fact]
    public void Linkify_PathInProse_BecomesALink() =>
        Assert.Equal(
            "See [src/Foo.cs](" + Link("src%2FFoo.cs") + ") for details.",
            ChatFileReference.LinkifyFileReferences("See src/Foo.cs for details."));

    // The overwhelmingly common shape in agent answers: the path is already marked as a literal.
    // The link has to wrap the code span rather than replace it, or the reference stops looking
    // like a path and the surrounding prose reflows.
    [Fact]
    public void Linkify_PathInCodeSpan_KeepsTheCodeSpanInsideTheLink() =>
        Assert.Equal(
            "See [`src/Foo.cs`](" + Link("src%2FFoo.cs") + ").",
            ChatFileReference.LinkifyFileReferences("See `src/Foo.cs`."));

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
    [InlineData("src/Foo.cs:12", "src%2FFoo.cs", 12)]
    [InlineData("src/Foo.cs:12:5", "src%2FFoo.cs", 12)]
    [InlineData("src/Foo.cs(12,5)", "src%2FFoo.cs", 12)]
    public void Linkify_CommonLocationSuffixes_YieldThePathAndTheLine(string token, string encoded, int line) =>
        Assert.Equal(
            "[" + token + "](" + Link(encoded, line) + ")",
            ChatFileReference.LinkifyFileReferences(token));

    // A Windows separator is a markdown escape character in link text: unescaped, "src\.editorconfig"
    // would render as "src.editorconfig" and "\F" would be one backslash the reader cannot trust.
    [Fact]
    public void Linkify_WindowsPathInProse_EscapesTheSeparatorInTheLinkText() =>
        Assert.Equal(
            @"Edit [src\\Foo.cs](" + Link("src%5CFoo.cs") + ").",
            ChatFileReference.LinkifyFileReferences(@"Edit src\Foo.cs."));

    // Sentence punctuation is not part of the reference; swallowing it produces a path that can
    // never resolve.
    [Fact]
    public void Linkify_TrailingSentencePunctuation_StaysOutsideTheLink() =>
        Assert.Equal(
            "Look at [src/Foo.cs](" + Link("src%2FFoo.cs") + "), then stop.",
            ChatFileReference.LinkifyFileReferences("Look at src/Foo.cs, then stop."));

    // Rewriting inside a fence would show the reader "[src/Foo.cs](/__claudecode/open?...)" as code.
    [Fact]
    public void Linkify_FencedCodeBlock_IsLeftAlone()
    {
        const string markdown = "Before src/A.cs\n```\nusing src/Foo.cs;\n```\nafter";
        Assert.Equal(
            "Before [src/A.cs](" + Link("src%2FA.cs") + ")\n```\nusing src/Foo.cs;\n```\nafter",
            ChatFileReference.LinkifyFileReferences(markdown));
    }

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
