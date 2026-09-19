using ClaudeCode.Core.ViewModels;
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
    public void TokenizeLine_ReassemblesToOriginalLine()
    {
        const string line = "  const total = price * 1.08; // tax-inclusive";

        var tokens = CodeHighlighter.TokenizeLine("javascript", line);

        Assert.Equal(line, string.Concat(tokens.Select(t => t.Text)));
    }
}
