using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Represents a view model containing tool call content details including text, path, and diff information.
/// </summary>
public sealed class ToolCallContentViewModel
{
    /// <summary>
    /// Initializes a new instance of the ToolCallContentViewModel class with the specified content and optional diff lines, validating the content parameter and processing the text and diff information accordingly.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <param name="diffLines">The collection of diff lines.</param>
    /// <exception cref="ArgumentNullException">Thrown when an error occurs during execution.</exception>
    public ToolCallContentViewModel(ToolCallContent content, IReadOnlyList<DiffLineViewModel>? diffLines = null)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        Text = StripFenceWrapper(content.Text);
        Path = content.Path;
        IsDiff = content.IsDiff;
        DiffLines = IsDiff ? diffLines ?? DiffBuilder.Build(content.OldText ?? string.Empty, content.NewText ?? string.Empty) : Array.Empty<DiffLineViewModel>();
    }

    /// <summary>
    /// Gets the text.
    /// </summary>
    public string? Text { get; }

    /// <summary>
    /// Gets the path.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// Gets a value indicating whether is diff.
    /// </summary>
    public bool IsDiff { get; }

    /// <summary>
    /// Gets the collection of diff lines.
    /// </summary>
    public IReadOnlyList<DiffLineViewModel> DiffLines { get; }

    private static readonly char[] _infoStringDisallowedChars = { ' ', '\t', '`' };

    /// <summary>
    /// Session-resume replay from the ACP agent sometimes wraps a tool call's plain-text content in
    /// a Markdown fenced code block (e.g. "```console\n...\n```") - a convention meant for a Markdown
    /// renderer. The transcript renders this text verbatim (see buildPlainBody in
    /// Resources/Transcript/transcript.js), so left alone the fence markers would show up as literal
    /// text. Strip a wrapper only when the trailing fence provably closes the leading one and so
    /// spans the *entire* text; anything else - two separate blocks, a fence appearing only in part
    /// of the text, a closing run shorter than the opening one, or a close that does not start its
    /// own line - is left untouched. Content the agent printed is never worth deleting to hide a
    /// fence marker, so every ambiguous case returns the input unchanged.
    /// </summary>
    internal static string? StripFenceWrapper(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var trimmed = text!.TrimEnd();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var openingFenceLength = 0;
        while (openingFenceLength < trimmed.Length && trimmed[openingFenceLength] == '`')
        {
            openingFenceLength++;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text;
        }

        var infoString = trimmed.Substring(openingFenceLength, firstNewline - openingFenceLength);
        if (infoString.IndexOfAny(_infoStringDisallowedChars) >= 0)
        {
            return text;
        }

        // The closing fence is the trailing backtick run. CommonMark lets it be longer than the
        // opening fence but never shorter, and it must start its own line.
        var closingFenceStart = trimmed.Length;
        while (closingFenceStart > 0 && trimmed[closingFenceStart - 1] == '`')
        {
            closingFenceStart--;
        }

        if (trimmed.Length - closingFenceStart < openingFenceLength
            || closingFenceStart <= firstNewline
            || trimmed[closingFenceStart - 1] != '\n')
        {
            return text;
        }

        var inner = trimmed.Substring(firstNewline + 1, closingFenceStart - (firstNewline + 1));
        if (ContainsClosingFenceLine(inner, openingFenceLength))
        {
            // An earlier fence may already have closed the leading one (two separate blocks), so the
            // trailing fence is not provably this block's close. Leave the payload alone.
            return text;
        }

        if (inner.EndsWith("\r\n", StringComparison.Ordinal))
        {
            inner = inner.Substring(0, inner.Length - 2);
        }
        else if (inner.EndsWith("\n", StringComparison.Ordinal))
        {
            inner = inner.Substring(0, inner.Length - 1);
        }

        return inner;
    }

    /// <summary>CommonMark closes a backtick fence only with a line that, after at most three
    /// spaces of indentation, is a run of at least the opening length of backticks and nothing but
    /// blanks after it. Backticks anywhere else in a line - a numbered Read of a README carrying
    /// "5\t```bash" - cannot have closed the opener, so they do not make the wrapper ambiguous.</summary>
    private static bool ContainsClosingFenceLine(string inner, int openingFenceLength)
    {
        var lineStart = 0;
        while (lineStart < inner.Length)
        {
            var lineEnd = inner.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = inner.Length;
            }

            if (IsClosingFenceLine(inner, lineStart, lineEnd, openingFenceLength))
            {
                return true;
            }

            lineStart = lineEnd + 1;
        }

        return false;
    }

    /// <summary>
    /// Determines whether the specified text segment represents a closing fence line by validating the backtick count and trailing whitespace.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The start.</param>
    /// <param name="end">The end.</param>
    /// <param name="openingFenceLength">The opening fence length.</param>
    /// <returns>true if the condition is met; otherwise, false.</returns>
    private static bool IsClosingFenceLine(string text, int start, int end, int openingFenceLength)
    {
        var index = start;
        while (index < end && index - start < 3 && text[index] == ' ')
        {
            index++;
        }

        var runStart = index;
        while (index < end && text[index] == '`')
        {
            index++;
        }

        if (index - runStart < openingFenceLength)
        {
            return false;
        }

        while (index < end && (text[index] == ' ' || text[index] == '\t' || text[index] == '\r'))
        {
            index++;
        }

        return index == end;
    }
}
