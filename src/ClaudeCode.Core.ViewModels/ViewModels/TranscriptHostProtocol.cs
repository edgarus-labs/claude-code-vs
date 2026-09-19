using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Host-side rules for the WebView2 transcript page: which document is allowed to talk to the host,
/// how its zoom messages map onto the base font size, and which view-model changes actually alter
/// what the page shows. Kept free of WPF/WebView2/JSON types so the security- and correctness-
/// relevant decisions are directly unit-testable; <c>ClaudeCode.Core.Views.ChatPanelView</c> owns
/// only the CoreWebView2 plumbing that feeds them.
/// </summary>
public static class TranscriptHostProtocol
{
    /// <summary>Virtual host name mapped to the transcript's asset folder.</summary>
    public const string VirtualHostName = "claudecode.transcript";

    /// <summary>The one document the transcript frame is ever allowed to show.</summary>
    public const string PageUrl = "https://" + VirtualHostName + "/index.html";

    /// <summary>Smallest transcript/composer base font size Ctrl+wheel and page zoom can reach.</summary>
    public const double MinFontSize = 10d;

    /// <summary>Largest transcript/composer base font size Ctrl+wheel and page zoom can reach.</summary>
    public const double MaxFontSize = 28d;

    /// <summary>
    /// True only for the transcript page's own origin. Used both to cancel navigation away from the
    /// page and to reject web messages from any other document: the host pushes the entire
    /// conversation into whatever document occupies the frame, so a foreign one must never become
    /// that target or be able to drive the host. Fails closed - a non-absolute, non-https, ported
    /// or credentialed URL is rejected, and a look-alike host such as
    /// <c>https://claudecode.transcript.example.com/</c> does not match.
    /// </summary>
    public static bool IsTranscriptOrigin(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri parsed) &&
        parsed.Scheme == Uri.UriSchemeHttps &&
        parsed.IsDefaultPort &&
        parsed.UserInfo.Length == 0 &&
        string.Equals(parsed.Host, VirtualHostName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies one zoom step to the base font size, clamped to the supported range. The
    /// page reports a DOM wheel delta, whose sign is the opposite of WPF's Ctrl+wheel convention
    /// (DOM deltaY &gt; 0 is "scroll down" = zoom out), so pass <paramref name="step"/> already in
    /// WPF's sign convention: positive enlarges.</summary>
    public static double StepFontSize(double size, double step) =>
        ClampFontSize(size + Math.Sign(step));

    /// <summary>Clamps a base font size to the supported range.</summary>
    public static double ClampFontSize(double size) => Math.Max(MinFontSize, Math.Min(MaxFontSize, size));

    /// <summary>
    /// True when a <see cref="ChatMessageViewModel"/> or <see cref="ToolCallCardViewModel"/> property
    /// change alters what the transcript page displays, so the host must repaint. Streamed turns
    /// mutate these view models in place without touching <c>ChatViewModel.Messages</c>, and a turn
    /// driven from claude.ai/code (Remote Control) never sets <c>IsBusy</c>, so this - not an
    /// activity poll - is what makes such a turn visible. Anything the transcript payload does not
    /// carry must return false: <see cref="ToolCallCardViewModel.IsExpanded"/> is WPF-only state the
    /// page manages itself, and repainting for it would re-push the whole conversation for nothing.
    /// </summary>
    public static bool AffectsTranscript(string? propertyName) => propertyName is
        nameof(ChatMessageViewModel.Text) or
        nameof(ChatMessageViewModel.DurationSeconds) or
        nameof(ChatMessageViewModel.TokensUsed) or
        nameof(ToolCallCardViewModel.Title) or
        nameof(ToolCallCardViewModel.Status);
}
