using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;

namespace ClaudeCode.Vsix;

/// <summary>
/// Caret placement shared by every "open this document at a location" path in the extension: the
/// VsControl <c>openDocument</c> command and the chat transcript's clickable file references. Both
/// receive a 1-based line from an untrusted source (an MCP client, or a path the agent typed into
/// its answer), so the line is clamped into the buffer rather than trusted - a stale or invented
/// line number must scroll somewhere sane, never throw out of a UI-thread continuation.
/// </summary>
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
