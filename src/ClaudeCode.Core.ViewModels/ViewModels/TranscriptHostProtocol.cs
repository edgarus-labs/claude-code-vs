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

    /// <summary>Base font size the transcript and composer start at, and the size every hardcoded
    /// design value in the views was authored against.</summary>
    public const double DefaultFontSize = 13d;

    /// <summary>
    /// How long the host may keep collapsing a burst of transcript changes into one repaint.
    /// Session resume replays every past message as its own change notification and a streaming
    /// turn raises one per chunk; repainting each is O(n^2) work for an n-message history.
    /// </summary>
    public static readonly TimeSpan RenderCoalesceWindow = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// The longest the transcript may stay stale while changes keep arriving. Coalescing alone is
    /// not enough: chunks that arrive closer together than <see cref="RenderCoalesceWindow"/>
    /// restart the window before it can elapse, so a turn driven from claude.ai/code - which never
    /// sets <c>IsBusy</c> and therefore never starts the host's one-second activity timer - would
    /// otherwise show nothing at all until the stream went quiet.
    /// </summary>
    public static readonly TimeSpan MaxRenderInterval = TimeSpan.FromMilliseconds(250);

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
    /// Scales a value authored against <see cref="DefaultFontSize"/> so it keeps its proportion as
    /// the base text size changes: <paramref name="designValue"/> is the size (or width/height) the
    /// view was drawn with at the default, <paramref name="baseSize"/> the current base size.
    /// </summary>
    public static double ScaleFontSize(double baseSize, double designValue) =>
        baseSize * designValue / DefaultFontSize;

    /// <summary>
    /// True when a transcript change must be painted now instead of joining the coalescing window,
    /// because the page has already been stale for <see cref="MaxRenderInterval"/>.
    /// </summary>
    public static bool ShouldPaintImmediately(TimeSpan sinceLastPaint) => sinceLastPaint >= MaxRenderInterval;

    /// <summary>
    /// The absolute URI the host may hand to the user's browser for the agent-reported Remote
    /// Control session link, or <see langword="null"/> when the agent supplied anything else. The
    /// URL crosses the untrusted boundary and ends up at <c>ShellExecute</c>, so only absolute
    /// https qualifies: http would let a hostile agent point the pill at a plaintext endpoint, and
    /// a non-web scheme would hand an arbitrary shell verb to the OS.
    /// </summary>
    public static string? NormalizeRemoteControlLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) && parsed.Scheme == Uri.UriSchemeHttps
            ? parsed.AbsoluteUri
            : null;

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
        nameof(ChatMessageViewModel.Images) or
        nameof(ToolCallCardViewModel.Title) or
        nameof(ToolCallCardViewModel.Status);
}
