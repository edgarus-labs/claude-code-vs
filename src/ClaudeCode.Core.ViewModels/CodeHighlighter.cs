using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ClaudeCode.Core.ViewModels;

public enum CodeTokenKind
{
    Plain,
    Comment,
    String,
    Number,
    Keyword,
}

public readonly struct CodeToken
{
    public CodeToken(string text, CodeTokenKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public string Text { get; }

    public CodeTokenKind Kind { get; }
}

/// <summary>
/// A small, dependency-free approximation of syntax highlighting for fenced code blocks in the
/// chat transcript: comments, string literals, numbers, and a per-language keyword list. It is not
/// a real lexer (no per-language grammar, no nested/multi-line comments) - just enough to make code
/// blocks visually parseable at a glance, which is what the transcript needs it for. Lives in the
/// XAML-free ViewModels project (no WPF dependency) so it can be unit tested directly; MarkdownMessageView
/// (ClaudeCode.Core) only turns its output into colored Runs.
/// </summary>
public static class CodeHighlighter
{
    // Ordered so string/comment matches win over a keyword that happens to appear inside one.
    private static readonly Regex _tokenPattern = new Regex(
        @"(?<comment>//.*$|#.*$|--.*$)" +
        @"|(?<string>""(?:[^""\\\r\n]|\\.)*""|'(?:[^'\\\r\n]|\\.)*'|`(?:[^`\\\r\n]|\\.)*`)" +
        @"|(?<number>\b0[xX][0-9a-fA-F]+\b|\b\d+(?:\.\d+)?\b)" +
        @"|(?<word>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Dictionary<string, string> _lineCommentByLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = "#", ["py"] = "#", ["bash"] = "#", ["sh"] = "#", ["shell"] = "#", ["zsh"] = "#",
        ["powershell"] = "#", ["ps1"] = "#", ["yaml"] = "#", ["yml"] = "#", ["ruby"] = "#", ["rb"] = "#",
        ["toml"] = "#", ["dockerfile"] = "#", ["r"] = "#",
        ["sql"] = "--", ["lua"] = "--",
        ["csharp"] = "//", ["cs"] = "//", ["javascript"] = "//", ["js"] = "//", ["jsx"] = "//",
        ["typescript"] = "//", ["ts"] = "//", ["tsx"] = "//", ["java"] = "//", ["c"] = "//", ["cpp"] = "//",
        ["c++"] = "//", ["go"] = "//", ["rust"] = "//", ["rs"] = "//", ["swift"] = "//", ["kotlin"] = "//",
        ["kt"] = "//", ["php"] = "//", ["json5"] = "//",
    };

    private static readonly string[] _genericKeywords =
    {
        "if", "else", "elif", "for", "foreach", "while", "do", "switch", "case", "default", "break",
        "continue", "return", "yield", "function", "func", "def", "fn", "class", "struct", "interface",
        "enum", "trait", "impl", "namespace", "module", "import", "from", "export", "using", "package",
        "public", "private", "protected", "internal", "static", "readonly", "const", "let", "var",
        "new", "delete", "this", "self", "super", "extends", "implements", "override", "virtual",
        "abstract", "async", "await", "try", "catch", "finally", "throw", "throws", "null", "nil",
        "none", "true", "false", "void", "typeof", "instanceof", "in", "of", "as", "is", "not", "and", "or",
    };

    public static IReadOnlyList<CodeToken> TokenizeLine(string? language, string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return Array.Empty<CodeToken>();
        }

        string? commentPrefix = language is not null && _lineCommentByLanguage.TryGetValue(language.Trim(), out var prefix)
            ? prefix
            : null;

        var tokens = new List<CodeToken>();
        int cursor = 0;
        foreach (Match match in _tokenPattern.Matches(line))
        {
            if (match.Groups["comment"].Success && !IsRecognizedComment(match.Groups["comment"].Value, commentPrefix))
            {
                // Only trust a comment match for a language whose comment marker we actually know
                // (or, unlabeled, when it starts with a marker any supported language uses) -
                // otherwise "//" inside e.g. a URL in plain text would wrongly grey out the rest of the line.
                continue;
            }

            if (match.Index > cursor)
            {
                tokens.Add(new CodeToken(line.Substring(cursor, match.Index - cursor), CodeTokenKind.Plain));
            }

            if (match.Groups["comment"].Success)
            {
                tokens.Add(new CodeToken(match.Value, CodeTokenKind.Comment));
            }
            else if (match.Groups["string"].Success)
            {
                tokens.Add(new CodeToken(match.Value, CodeTokenKind.String));
            }
            else if (match.Groups["number"].Success)
            {
                tokens.Add(new CodeToken(match.Value, CodeTokenKind.Number));
            }
            else if (match.Groups["word"].Success && IsKeyword(match.Value))
            {
                tokens.Add(new CodeToken(match.Value, CodeTokenKind.Keyword));
            }
            else
            {
                tokens.Add(new CodeToken(match.Value, CodeTokenKind.Plain));
            }

            cursor = match.Index + match.Length;

            // A line comment consumes the rest of the line; nothing after it needs tokenizing.
            if (match.Groups["comment"].Success)
            {
                break;
            }
        }

        if (cursor < line.Length)
        {
            tokens.Add(new CodeToken(line.Substring(cursor), CodeTokenKind.Plain));
        }

        return tokens;
    }

    private static bool IsRecognizedComment(string commentText, string? knownPrefix)
    {
        if (knownPrefix is not null)
        {
            return commentText.StartsWith(knownPrefix, StringComparison.Ordinal);
        }

        // Unlabeled fence: still honor "//" since it is unambiguous and used by the most common
        // languages pasted into chat (JS/TS/C#/Java/Go/Rust/...).
        return commentText.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool IsKeyword(string word)
    {
        foreach (string keyword in _genericKeywords)
        {
            if (string.Equals(keyword, word, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
