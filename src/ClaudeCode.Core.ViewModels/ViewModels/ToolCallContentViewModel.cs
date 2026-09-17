using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;

namespace ClaudeCode.Core.ViewModels;

public sealed class ToolCallContentViewModel
{
    public ToolCallContentViewModel(ToolCallContent content)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        Text = content.Text;
        Path = content.Path;
        IsDiff = content.IsDiff;
        DiffLines = IsDiff ? DiffBuilder.Build(content.OldText ?? string.Empty, content.NewText ?? string.Empty) : Array.Empty<DiffLineViewModel>();
    }

    public string? Text { get; }

    public string? Path { get; }

    public bool IsDiff { get; }

    public IReadOnlyList<DiffLineViewModel> DiffLines { get; }
}
