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

    [Fact]
    public void Linkify_PathInProse_ShowsTheFileNameAndLinksTheFullPath() =>
        Assert.Equal(
            "See [Foo.cs](" + Link("src%2FFoo.cs") + ") for details.",
            ChatFileReference.LinkifyFileReferences("See src/Foo.cs for details."));

    [Fact]
    public void Linkify_PathInCodeSpan_KeepsTheCodeSpanInsideTheLink() =>
        Assert.Equal(
            "See [`Foo.cs`](" + Link("src%2FFoo.cs") + ").",
            ChatFileReference.LinkifyFileReferences("See `src/Foo.cs`."));

    [Fact]
    public void Linkify_AbsolutePathWithLineInCodeSpan_ShowsTheFileNameAndTheLine() =>
        Assert.Equal(
            "At [`Program.cs:12`](" + Link("C%3A%5Crepo%5Csrc%5CProgram.cs", 12) + ").",
            ChatFileReference.LinkifyFileReferences(@"At `C:\repo\src\Program.cs:12`."));

    [Theory]
    [InlineData(@"**`src\Acp\Factory.cs`**", "**[`Factory.cs`](", "src%5CAcp%5CFactory.cs", ")**")]
    [InlineData("__`src/Foo.cs`__", "__[`Foo.cs`](", "src%2FFoo.cs", ")__")]
    [InlineData("*`Foo.cs`*", "*[`Foo.cs`](", "Foo.cs", ")*")]
    [InlineData("***`Foo.cs`***.", "***[`Foo.cs`](", "Foo.cs", ")***.")]
    public void Linkify_EmphasisedCodeSpan_LinksInsideTheEmphasis(string token, string before, string encodedPath, string after) =>
        Assert.Equal(
            "1. " + before + Link(encodedPath) + after + " — the factory",
            ChatFileReference.LinkifyFileReferences("1. " + token + " — the factory"));

    [Theory]
    [InlineData(@"**src\Foo.cs**", "**[Foo.cs](", "src%5CFoo.cs", ")**")]
    [InlineData("_Program.cs:42_,", "_[Program.cs:42](", "Program.cs&line=42", ")_,")]
    public void Linkify_EmphasisedPathInProse_LinksInsideTheEmphasis(string token, string before, string encodedPathAndLine, string after) =>
        Assert.Equal(
            "See " + before + ChatFileReference.LinkPrefix + "path=" + encodedPathAndLine + after,
            ChatFileReference.LinkifyFileReferences("See " + token));

    [Theory]
    [InlineData("pkg/__init__.py", @"\_\_init\_\_.py", "pkg%2F__init__.py")]
    [InlineData("__init__.py:3", @"\_\_init\_\_.py:3", "__init__.py&line=3")]
    public void Linkify_PathWithUnderscoresInTheName_ShowsThemLiterallyAndKeepsThemInThePath(string token, string shown, string encodedPathAndLine) =>
        Assert.Equal(
            "[" + shown + "](" + ChatFileReference.LinkPrefix + "path=" + encodedPathAndLine + ")",
            ChatFileReference.LinkifyFileReferences(token));

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

    [Fact]
    public void Linkify_BareFileNameInCodeSpan_BecomesALink() =>
        Assert.Equal(
            "Open [`README.md`](" + Link("README.md") + ").",
            ChatFileReference.LinkifyFileReferences("Open `README.md`."));

    [Theory]
    [InlineData("We use Node.js here.")]
    [InlineData("Runs on asp.net today.")]
    public void Linkify_BareFileNameInProse_IsLeftAlone(string markdown) =>
        Assert.Equal(markdown, ChatFileReference.LinkifyFileReferences(markdown));

    [Fact]
    public void Linkify_BareFileNameWithLineInProse_BecomesALink() =>
        Assert.Equal(
            "Fails at [Program.cs:42](" + Link("Program.cs", 42) + ").",
            ChatFileReference.LinkifyFileReferences("Fails at Program.cs:42."));

    [Theory]
    [InlineData("src/Foo.cs:12", "Foo.cs:12", "src%2FFoo.cs", 12)]
    [InlineData("src/Foo.cs:12:5", "Foo.cs:12:5", "src%2FFoo.cs", 12)]
    [InlineData("src/Foo.cs(12,5)", "Foo.cs(12,5)", "src%2FFoo.cs", 12)]
    public void Linkify_CommonLocationSuffixes_YieldThePathAndTheLine(string token, string shown, string encoded, int line) =>
        Assert.Equal(
            "[" + shown + "](" + Link(encoded, line) + ")",
            ChatFileReference.LinkifyFileReferences(token));

    [Fact]
    public void Linkify_WindowsPathInProse_ShowsTheFileNameAndLinksTheFullPath() =>
        Assert.Equal(
            "Edit [Foo.cs](" + Link("src%5CFoo.cs") + ").",
            ChatFileReference.LinkifyFileReferences(@"Edit src\Foo.cs."));

    [Theory]
    [InlineData("docs/a].md", @"a\].md", "docs%2Fa%5D.md")]
    [InlineData("docs/[a].md", @"\[a\].md", "docs%2F%5Ba%5D.md")]
    public void Linkify_PathWithBracketsInProse_EscapesThemInTheLinkText(
        string path, string expectedText, string encodedPath) =>
        Assert.Equal(
            "Open [" + expectedText + "](" + Link(encodedPath) + ").",
            ChatFileReference.LinkifyFileReferences("Open " + path + "."));

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

    [Fact]
    public void Linkify_TrailingSentencePunctuation_StaysOutsideTheLink() =>
        Assert.Equal(
            "Look at [Foo.cs](" + Link("src%2FFoo.cs") + "), then stop.",
            ChatFileReference.LinkifyFileReferences("Look at src/Foo.cs, then stop."));

    [Fact]
    public void Linkify_FencedCodeBlock_IsLeftAlone()
    {
        const string markdown = "Before src/A.cs\n```\nusing src/Foo.cs;\n```\nafter";
        Assert.Equal(
            "Before [A.cs](" + Link("src%2FA.cs") + ")\n```\nusing src/Foo.cs;\n```\nafter",
            ChatFileReference.LinkifyFileReferences(markdown));
    }

    [Theory]
    [InlineData("~~~", "")]
    [InlineData("```", "   ")]
    [InlineData("~~~", "   ")]
    public void Linkify_FenceMarkerVariants_LeaveTheBlockAloneAndStillClose(string fence, string indent) =>
        Assert.Equal(
            indent + fence + "\nusing src/Foo.cs;\n" + indent + fence + "\nsee [A.cs](" + Link("src%2FA.cs") + ")",
            ChatFileReference.LinkifyFileReferences(
                indent + fence + "\nusing src/Foo.cs;\n" + indent + fence + "\nsee src/A.cs"));

    [Fact]
    public void Linkify_ForeignFenceMarkerInsideAFence_DoesNotCloseIt() =>
        Assert.Equal(
            "~~~\n```\nusing src/Foo.cs;\n~~~\nsee [A.cs](" + Link("src%2FA.cs") + ")",
            ChatFileReference.LinkifyFileReferences("~~~\n```\nusing src/Foo.cs;\n~~~\nsee src/A.cs"));

    [Fact]
    public void Linkify_IndentedCodeBlock_IsLeftAlone() =>
        Assert.Equal(
            "text\n\n    open src/Foo.cs\n",
            ChatFileReference.LinkifyFileReferences("text\n\n    open src/Foo.cs\n"));

    [Theory]
    [InlineData("[src/Foo.cs](https://example.com/x)")]
    [InlineData("![diagram](docs/diagram.png)")]
    public void Linkify_ExistingMarkdownLink_IsLeftAlone(string markdown) =>
        Assert.Equal(markdown, ChatFileReference.LinkifyFileReferences(markdown));

    [Fact]
    public void Linkify_LinkWithAnOverlongDestination_IsCopiedThroughVerbatim()
    {
        var destination = new string('a', 600) + "/b.md";
        Assert.Equal(
            "[text](" + destination + " \"T\") and [A.cs](" + Link("src%2FA.cs") + ")",
            ChatFileReference.LinkifyFileReferences("[text](" + destination + " \"T\") and src/A.cs"));
    }

    [Theory]
    [InlineData("See https://example.com/docs/guide.html for more.")]
    [InlineData("See <https://example.com/docs/guide.html> for more.")]
    public void Linkify_Url_IsLeftAlone(string markdown) =>
        Assert.Equal(markdown, ChatFileReference.LinkifyFileReferences(markdown));

    [Theory]
    [InlineData("Run `dotnet build` now.")]
    [InlineData("Use `var x = a.b;` here.")]
    [InlineData("Version `1.0.0` shipped.")]
    [InlineData("Tag `v1.2` shipped.")]
    [InlineData("Call `list.Add` twice.")]
    public void Linkify_CodeSpanThatIsNotAPath_IsLeftAlone(string markdown) =>
        Assert.Equal(markdown, ChatFileReference.LinkifyFileReferences(markdown));

    [Theory]
    [InlineData("See `https://example.com/docs/guide.html` for more.")]
    [InlineData("See `http://example.com/docs/guide.html` for more.")]
    [InlineData("Pass `--out=foo.json` to it.")]
    [InlineData("Set `key=value.yml` there.")]
    public void Linkify_CodeSpanThatIsAUrlOrAnAssignment_IsLeftAlone(string markdown) =>
        Assert.Equal(markdown, ChatFileReference.LinkifyFileReferences(markdown));

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
