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

    /// <summary>
    /// Session-resume replay from the ACP agent sometimes wraps a tool call's plain-text content in
    /// a Markdown fenced code block (e.g. "```console\n...\n```") - a convention meant for a Markdown
    /// renderer. This view is a plain, already-monospaced TextBlock (see ToolCallContentTemplate in
    /// ChatPanelView.xaml), so left alone the fence markers would show up as literal text. Strip a
    /// wrapper that spans the *entire* text; anything else (fences appearing only in part of the
    /// text) is left untouched, since that is presumably real content, not a wrapper.
    /// </summary>
    private static readonly char[] _infoStringDisallowedChars = { ' ', '\t' };

    internal static string? StripFenceWrapper(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var trimmed = text!.TrimEnd();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text;
        }

        var infoString = trimmed.Substring(3, firstNewline - 3);
        if (infoString.IndexOfAny(_infoStringDisallowedChars) >= 0)
        {
            return text;
        }

        var closingFenceStart = trimmed.Length - 3;
        if (closingFenceStart <= firstNewline + 1)
        {
            return text;
        }

        var inner = trimmed.Substring(firstNewline + 1, closingFenceStart - (firstNewline + 1));
        if (inner.EndsWith("\n", StringComparison.Ordinal))
        {
            inner = inner.Substring(0, inner.Length - 1);
        }

        return inner;
    }
}
