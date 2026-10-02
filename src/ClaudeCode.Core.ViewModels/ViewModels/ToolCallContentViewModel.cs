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
