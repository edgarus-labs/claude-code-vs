using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;

namespace ClaudeCode.Core.ViewModels;

public sealed class ToolCallContentViewModel
{
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

    public string? Text { get; }

    public string? Path { get; }

    public bool IsDiff { get; }

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
        if (inner.IndexOf("```", StringComparison.Ordinal) >= 0)
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
}
