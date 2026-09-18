namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Pure auto-scroll decision for <c>ClaudeCode.Core.Views.ChatPanelView</c>'s transcript
/// <c>ScrollViewer</c>. Kept here (XAML-free) so it is directly unit-testable.
/// </summary>
public static class ChatTranscriptScrollPolicy
{
    /// <summary>
    /// True only when new content was added (extent grew) while the user was already scrolled to
    /// the bottom: never yanks the viewport out from under someone who scrolled up to read history.
    /// </summary>
    public static bool ShouldAutoScroll(bool wasAtBottom, double extentHeightChange) =>
        wasAtBottom && extentHeightChange > 0;
}
