using System;
using System.Collections.Generic;
using ClaudeCode.Contracts;

namespace ClaudeCode.Core.ViewModels
{
    /// <summary>Renders one <see cref="ToolCallContent"/> entry: either a unified diff or plain text.</summary>
    public sealed class ToolCallContentViewModel
    {
        public ToolCallContentViewModel(ToolCallContent content)
        {
            if (content == null)
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
}
