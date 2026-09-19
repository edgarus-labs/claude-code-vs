using ClaudeCode.Core.ViewModels;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class CodeHighlighterTests
{
    [Fact]
    public void TokenizeLine_EmptyLine_ReturnsNoTokens()
    {
        var tokens = CodeHighlighter.TokenizeLine("csharp", "");

        Assert.Empty(tokens);
    }

    [Fact]
    public void TokenizeLine_RecognizesKeywordStringAndNumber()
    {
        var tokens = CodeHighlighter.TokenizeLine("csharp", "return \"ok\" + 42;");

        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && t.Text == "return");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.String && t.Text == "\"ok\"");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Number && t.Text == "42");
    }

    [Fact]
    public void TokenizeLine_KnownLanguageCommentMarker_MarksRestOfLineAsComment()
    {
        var tokens = CodeHighlighter.TokenizeLine("python", "x = 1  # trailing note");

        var comment = Assert.Single(tokens, t => t.Kind == CodeTokenKind.Comment);
        Assert.Equal("# trailing note", comment.Text);
    }

    [Fact]
    public void TokenizeLine_HashInUnknownLanguage_IsNotTreatedAsComment()
    {
        // "#" is Python/bash's comment marker but not, say, C#'s - an unlabeled or C#-fenced line
        // containing "#" (e.g. a hex color or a preprocessor directive) must not be greyed out.
        var tokens = CodeHighlighter.TokenizeLine("csharp", "#region Foo");

        Assert.DoesNotContain(tokens, t => t.Kind == CodeTokenKind.Comment);
    }

    [Fact]
    public void TokenizeLine_UnlabeledLanguage_StillHonorsDoubleSlashComment()
    {
        var tokens = CodeHighlighter.TokenizeLine(null, "value(); // note");

        var comment = Assert.Single(tokens, t => t.Kind == CodeTokenKind.Comment);
        Assert.Equal("// note", comment.Text);
    }

    [Fact]
    public void TokenizeLine_DecrementOperator_DoesNotSwallowTheRestOfTheLine()
    {
        // "--" is SQL/Lua's comment marker, not C#'s: a decrement must leave the rest of the line
        // tokenized instead of being dropped as one undifferentiated plain run.
        var tokens = CodeHighlighter.TokenizeLine("csharp", "count--; return 42;");

        Assert.DoesNotContain(tokens, t => t.Kind == CodeTokenKind.Comment);
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && t.Text == "return");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Number && t.Text == "42");
    }

    [Fact]
    public void TokenizeLine_BashLongFlag_KeepsTokenizingAndStillFindsTheRealComment()
    {
        var tokens = CodeHighlighter.TokenizeLine("bash", "grep --color \"needle\" # find it");

        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.String && t.Text == "\"needle\"");
        var comment = Assert.Single(tokens, t => t.Kind == CodeTokenKind.Comment);
        Assert.Equal("# find it", comment.Text);
    }

    [Fact]
    public void TokenizeLine_ReassemblesToOriginalLine()
    {
        const string line = "  const total = price * 1.08; // tax-inclusive";

        var tokens = CodeHighlighter.TokenizeLine("javascript", line);

        Assert.Equal(line, string.Concat(tokens.Select(t => t.Text)));
    }

    [Fact]
    public void TokenizeLine_PathologicalLine_FallsBackToPlainTextInsteadOfStallingTheUiThread()
    {
        // Unterminated quotes make the string alternative rescan to end-of-line from every quote,
        // which is quadratic: at MarkdownSafetyLimits.MaxMarkdownLength (the largest message the
        // renderer will hand over) tokenizing this line takes ~20s with no match timeout - all of
        // it synchronous on the WPF UI thread. The regex deadline must cut it short and render the
        // line unhighlighted.
        var line = string.Concat(Enumerable.Repeat("\"\\", MarkdownSafetyLimits.MaxMarkdownLength / 2)) + " return 42";

        var stopwatch = Stopwatch.StartNew();
        var tokens = CodeHighlighter.TokenizeLine("csharp", line);
        stopwatch.Stop();

        var only = Assert.Single(tokens);
        Assert.Equal(CodeTokenKind.Plain, only.Kind);
        Assert.Equal(line, only.Text);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            "tokenization ran past the match deadline: " + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms");
    }
}
