using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;

namespace ClaudeCode.Vsix;

internal static class EditorCaret
{
    internal static void MoveToLine(IWpfTextView textView, ITextBuffer textBuffer, int line)
    {
        var snapshot = textBuffer.CurrentSnapshot;
        var lineNumber = Math.Max(0, Math.Min(line - 1, snapshot.LineCount - 1));
        var textLine = snapshot.GetLineFromLineNumber(lineNumber);
        textView.Caret.MoveTo(textLine.Start);
        textView.ViewScroller.EnsureSpanVisible(new SnapshotSpan(textLine.Start, 0));
    }
}
