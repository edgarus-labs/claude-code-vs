using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using ClaudeCode.Core.ViewModels.Demo;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClaudeCode.Core.Views;

public partial class ChatPanelView : UserControl, IDisposable
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private const long MaxImagePixels = 20_000_000;
    private const int MaxImages = 5;
    private const int CopyFeedbackDisplayMilliseconds = 4000;
    private const string TranscriptLostMessage =
        "The transcript display stopped updating after a Microsoft Edge WebView2 process failure " +
        "and could not be restored.";
    // The page itself answered with an HTTP error: no process failed, the transcript assets are not
    // where the extension expects them (a broken install), and reloading cannot change that.
    private const string TranscriptUnavailableMessage =
        "The transcript could not be displayed: the transcript page is missing from this " +
        "installation of the extension. Repair or reinstall the extension.";

    public static readonly DependencyProperty ChatTextFontSizeProperty = DependencyProperty.Register(
        nameof(ChatTextFontSize), typeof(double), typeof(ChatPanelView),
        new FrameworkPropertyMetadata(TranscriptHostProtocol.DefaultFontSize, OnChatTextFontSizeChanged));

    public static Func<IChatSessionServices>? ServicesFactory { get; set; }

    private readonly ChatViewModel _viewModel;
    private readonly DispatcherTimer _copyFeedbackTimer;
    private readonly DispatcherTimer _transcriptRenderTimer;
    private readonly DispatcherTimer _transcriptRenderDebounceTimer;
    private readonly List<ChatMessageViewModel> _trackedMessages = new List<ChatMessageViewModel>();
    private readonly List<ToolCallCardViewModel> _trackedCards = new List<ToolCallCardViewModel>();
    // Per-message JSON of its attachments. Images are fixed at send time and can run to 5 x 5 MB
    // of base64 per message, and every streamed chunk re-serializes the whole conversation, so
    // without this each 250 ms tick of a turn re-escaped every past attachment on the UI thread.
    private readonly Dictionary<ChatMessageViewModel, string> _imagesJsonByMessage =
        new Dictionary<ChatMessageViewModel, string>();
    private bool _disposed;
    private bool _transcriptReady;
    private DateTimeOffset? _busyStartedAt;
    private string? _messagesJson;
    private string? _activityJson;
    private bool _messagesJsonStale = true;
    private DateTimeOffset _lastRenderAt = DateTimeOffset.MinValue;
    // Set when NavigationStarting cancels an off-origin navigation, so the NavigationCompleted
    // failure that cancel produces is not mistaken for the transcript itself failing to load.
    private ulong? _cancelledNavigationId;
    // One reload per successful load: if the reload we issued is itself what just failed, stop
    // rather than spinning navigate -> fail -> navigate on the UI thread.
    private bool _transcriptReloadAttempted;
    // The notice that must stay on screen for the rest of the panel's life (see ShowCopyFeedback).
    private string? _persistentFeedback;

    public ChatPanelView()
    {
        InitializeComponent();

        var services = ServicesFactory?.Invoke() ?? new NullChatSessionServices();
        _viewModel = new ChatViewModel(services);
        DataContext = _viewModel;
        CommandManager.AddPreviewCanExecuteHandler(ComposerBox, ComposerBox_PreviewCanExecute);
        CommandManager.AddPreviewExecutedHandler(ComposerBox, ComposerBox_PreviewExecuted);
        _copyFeedbackTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(CopyFeedbackDisplayMilliseconds)
        };
        _copyFeedbackTimer.Tick += OnCopyFeedbackTimerTick;
        // Content changes arrive as change notifications (see TrackTranscriptItems), not by polling:
        // a turn driven from claude.ai/code (Remote Control) never sets IsBusy, so an IsBusy-gated
        // poll made its streamed text and tool-call progress invisible. The one thing no view model
        // raises is the activity block's own elapsed-seconds counter, so this timer exists solely to
        // advance that - its interval is the counter's display granularity, one second.
        _transcriptRenderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _transcriptRenderTimer.Tick += OnTranscriptActivityTick;
        // Session-resume replays every past message as its own Messages.Add, and a streaming turn
        // raises one change per chunk - rendering synchronously on each is O(n^2) work for an
        // n-message history and was visibly slow. Coalesce instead: a burst arriving within one
        // tick collapses into a single render once it goes quiet. ScheduleTranscriptRender caps how
        // long that can defer a paint, because restarting this timer per chunk means a stream that
        // never goes quiet would otherwise never fire it at all.
        _transcriptRenderDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TranscriptHostProtocol.RenderCoalesceWindow
        };
        _transcriptRenderDebounceTimer.Tick += OnTranscriptRenderDebounceTick;
        _viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.PlanReviewRequested += OnPlanReviewRequested;
        TrackTranscriptItems();
        Unloaded += OnUnloaded;
        _ = InitializeTranscriptWebViewAsync();
    }

    /// <summary>Raised when Claude asks for approval of an implementation plan; the host opens a plan document for it.</summary>
    public event EventHandler<PlanReviewViewModel>? PlanReviewRequested;

    /// <summary>Forwarded from the view model: the conversation needs the user back (see <see cref="ChatAttentionEventArgs"/>).</summary>
    public event EventHandler<ChatAttentionEventArgs>? AttentionRequested
    {
        add => _viewModel.AttentionRequested += value;
        remove => _viewModel.AttentionRequested -= value;
    }

    private void OnPlanReviewRequested(object? sender, EventArgs e)
    {
        if (!_disposed && _viewModel.PendingPlan is PlanReviewViewModel plan)
        {
            PlanReviewRequested?.Invoke(this, plan);
        }
    }

    public double ChatTextFontSize
    {
        get => (double)GetValue(ChatTextFontSizeProperty);
        private set => SetValue(ChatTextFontSizeProperty, value);
    }

    private static void OnChatTextFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ChatPanelView)d).PushFontSize();

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        ApplyCtrlWheelZoom(e);
    }

    // Popups (SlashPopup/ModelPopup/ModePopup/HistoryPopup) render their content in a separate
    // top-level PopupRoot window when AllowsTransparency="True", so mouse wheel events over them
    // never tunnel through ChatPanelView.OnPreviewMouseWheel above. Each popup's root Border wires
    // PreviewMouseWheel to this same handler so Ctrl+wheel zoom works there too.
    private void Popup_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => ApplyCtrlWheelZoom(e);

    private void ApplyCtrlWheelZoom(MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.Control || e.Delta == 0)
        {
            return;
        }

        // Intercept before any transcript/composer child scrolls or reroutes the wheel.
        // Even at a limit, keep Ctrl+wheel inside this pane rather than zooming its host.
        e.Handled = true;
        ChatTextFontSize = TranscriptHostProtocol.StepFontSize(ChatTextFontSize, e.Delta);
    }

    private async Task InitializeTranscriptWebViewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, WebView2Profile.UserDataFolder).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            await TranscriptView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            // Dropping a file from Explorer or Solution Explorer onto the transcript would otherwise
            // navigate the frame to file:///... - and since Source is assigned exactly once, the
            // transcript would be dead for the rest of the session while the host kept pushing the
            // whole conversation into the foreign document.
            TranscriptView.AllowExternalDrop = false;
            CoreWebView2 core = TranscriptView.CoreWebView2;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // Ctrl+S/Ctrl+P/Ctrl+F/F5 belong to the IDE, not to Chromium, inside a tool window.
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            // Nothing is ever exposed via AddHostObjectToScript; don't leave the door that permits it open.
            core.Settings.AreHostObjectsAllowed = false;
            // The WPF ChatTextFontSize is the single source of truth for the transcript's scale (the
            // page forwards Ctrl+wheel as a "zoom" message so transcript and composer scale together).
            // Chromium's own zoom - Ctrl+plus/minus, touchpad and touch pinch - would scale the page
            // alone and desynchronise the two until VS is restarted.
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            // Deny: nothing outside this page's own origin may load resources through this mapping.
            core.SetVirtualHostNameToFolderMapping(
                TranscriptHostProtocol.VirtualHostName, GetTranscriptAssetsPath(),
                CoreWebView2HostResourceAccessKind.Deny);
            core.WebMessageReceived += OnTranscriptWebMessageReceived;
            core.NewWindowRequested += OnTranscriptNewWindowRequested;
            core.NavigationStarting += OnTranscriptNavigationStarting;
            core.NavigationCompleted += OnTranscriptNavigationCompleted;
            core.ProcessFailed += OnTranscriptProcessFailed;
            TranscriptView.Source = new Uri(TranscriptHostProtocol.PageUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most likely causes: the WebView2 Runtime isn't installed, or the transcript assets
            // weren't deployed next to this assembly (SetVirtualHostNameToFolderMapping rejects the
            // relative path GetTranscriptAssetsPath falls back to). Either way the transcript area
            // stays blank, so say so instead of leaving the user staring at nothing - this task is
            // fire-and-forget, so an escaping exception would be an unobserved, undiagnosable fault.
            if (!_disposed)
            {
                ShowCopyFeedback(
                    "The transcript could not be displayed. The Microsoft Edge WebView2 Runtime is " +
                    "required: " + ex.Message, persistent: true);
            }
        }
    }

    private static string GetTranscriptAssetsPath() => Path.Combine(
        Path.GetDirectoryName(typeof(ChatPanelView).Assembly.Location) ?? string.Empty, "Resources", "Transcript");

    // Sanitized, local-only content never legitimately opens a new window/tab; block it outright.
    private void OnTranscriptNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) =>
        e.Handled = true;

    private void OnTranscriptNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Path-exact, not origin-level: plan.html is served from this same origin (both views map
        // the same asset folder), and a drop of an agent-authored link to it from inside the page
        // is not an external drop, so AllowExternalDrop does not cover it. Loading it would leave
        // a "successful" navigation with no window.claudeTranscript for the host to render into.
        if (TranscriptHostProtocol.IsTranscriptOrigin(e.Uri) &&
            Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) &&
            string.Equals(uri.GetLeftPart(UriPartial.Path), TranscriptHostProtocol.PageUrl, StringComparison.Ordinal))
        {
            return;
        }

        // Cancelling leaves the transcript document exactly where it was - the point is that it is
        // never replaced. Remember the id: the cancel still raises NavigationCompleted with
        // IsSuccess=false, and treating that as "the transcript failed to load" turned the origin
        // guard doing its job into a permanently blank panel.
        e.Cancel = true;
        _cancelledNavigationId = e.NavigationId;
    }

    // ProcessFailed covers far more than a fatal crash: GpuProcessExited (a display-driver TDR or
    // an Edge Evergreen update under a running devenv), RenderProcessUnresponsive (a long
    // highlight pass, which this page does by design), utility and audio process exits. WebView2
    // recovers from all of those itself and the document survives, so dropping the ready latch for
    // them froze the transcript for the rest of the session while the agent kept answering into
    // it. Only the renderer actually dying loses the document - and that one is recoverable.
    private void OnTranscriptProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                ReloadTranscriptPage(TranscriptLostMessage);
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The whole CoreWebView2 is gone: Navigate would throw and there is no document to
                // reload into. Fail closed so nothing pushes into a dead COM object, and say so
                // rather than letting the user type into a page that will never update again.
                InvalidateTranscriptPage();
                ShowCopyFeedback(TranscriptLostMessage, persistent: true);
                break;
        }
    }

    private void OnTranscriptNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!e.IsSuccess)
        {
            if (e.NavigationId == _cancelledNavigationId)
            {
                // Our own origin guard cancelled this one; the transcript is still live.
                _cancelledNavigationId = null;
                return;
            }

            // Never leave a stale "ready" latch behind a failed load - and never leave the panel
            // dead either: this is the only place _transcriptReady is ever re-latched. An HTTP
            // error is the page answering, not a process failing: say what is actually wrong.
            ReloadTranscriptPage(e.HttpStatusCode >= 400 ? TranscriptUnavailableMessage : TranscriptLostMessage);
            return;
        }

        _cancelledNavigationId = null;
        _transcriptReloadAttempted = false;
        _transcriptReady = true;
        PushTheme();
        PushFontSize();
        RenderTranscript();
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            // Reset (New Chat) and removals drop messages; an Add leaves every cached one valid.
            _imagesJsonByMessage.Clear();
        }

        TrackTranscriptItems();
        ScheduleTranscriptRender();
    }

    // Streamed content mutates existing view models in place - ChatMessageViewModel.AppendText and
    // ToolCallCardViewModel.Apply - without touching Messages. IsBusy is set only by the local
    // composer (SendCoreAsync), so a Remote-Control turn arriving as session/update notifications
    // left the transcript frozen on whatever chunk happened to coincide with the Messages.Add.
    // Listening to the items themselves covers both cases and costs nothing when idle, unlike a
    // permanently running poll timer.
    private void TrackTranscriptItems()
    {
        // Full resync rather than incremental add/remove bookkeeping: a Reset (New Chat clears
        // Messages) arrives with null OldItems, so incremental tracking would leak every handler.
        // It is O(items) on an event that already schedules an O(items) render.
        StopTrackingTranscriptItems();
        if (_disposed)
        {
            return;
        }

        foreach (ChatMessageViewModel message in _viewModel.Messages)
        {
            message.PropertyChanged += OnTranscriptItemPropertyChanged;
            message.Parts.CollectionChanged += OnTranscriptPartsChanged;
            _trackedMessages.Add(message);
            foreach (ChatMessagePart part in message.Parts)
            {
                if (part is ChatToolCallPart toolCall)
                {
                    toolCall.Card.PropertyChanged += OnTranscriptItemPropertyChanged;
                    toolCall.Card.Content.CollectionChanged += OnTranscriptContentChanged;
                    _trackedCards.Add(toolCall.Card);
                }
            }
        }
    }

    private void StopTrackingTranscriptItems()
    {
        foreach (ChatMessageViewModel message in _trackedMessages)
        {
            message.PropertyChanged -= OnTranscriptItemPropertyChanged;
            message.Parts.CollectionChanged -= OnTranscriptPartsChanged;
        }

        foreach (ToolCallCardViewModel card in _trackedCards)
        {
            card.PropertyChanged -= OnTranscriptItemPropertyChanged;
            card.Content.CollectionChanged -= OnTranscriptContentChanged;
        }

        _trackedMessages.Clear();
        _trackedCards.Clear();
    }

    private void OnTranscriptItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageViewModel.Images) && sender is ChatMessageViewModel message)
        {
            _imagesJsonByMessage.Remove(message);
        }

        if (TranscriptHostProtocol.AffectsTranscript(e.PropertyName))
        {
            ScheduleTranscriptRender();
        }
    }

    // A new part can bring a tool-call card that needs its own subscriptions; the content items
    // inside a card are immutable, so their collection only needs a repaint.
    private void OnTranscriptPartsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        TrackTranscriptItems();
        ScheduleTranscriptRender();
    }

    private void OnTranscriptContentChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScheduleTranscriptRender();

    private void ScheduleTranscriptRender()
    {
        _messagesJsonStale = true;
        _transcriptRenderDebounceTimer.Stop();

        // A streamed turn raises a change per chunk, far faster than the coalescing window, so
        // restarting the timer alone would keep deferring the paint for as long as the stream
        // lasts. A local turn has the one-second activity timer as a backstop; a turn driven from
        // claude.ai/code never sets IsBusy and so has none. Paint outright once the page has been
        // stale for MaxRenderInterval, which bounds that wait for both.
        if (TranscriptHostProtocol.ShouldPaintImmediately(DateTimeOffset.UtcNow - _lastRenderAt))
        {
            RenderTranscript();
            return;
        }

        _transcriptRenderDebounceTimer.Start();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.IsBusy):
                _busyStartedAt = _viewModel.IsBusy ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
                if (_viewModel.IsBusy)
                {
                    _transcriptRenderTimer.Start();
                }
                else
                {
                    _transcriptRenderTimer.Stop();
                }

                // Show/remove the activity indicator now, don't wait for the debounce or the tick.
                RenderTranscript();
                break;
            case nameof(ChatViewModel.ActivityText):
            case nameof(ChatViewModel.TurnTokens):
                RenderTranscript();
                break;
        }
    }

    private void OnTranscriptRenderDebounceTick(object? sender, EventArgs e)
    {
        _transcriptRenderDebounceTimer.Stop();
        RenderTranscript();
    }

    private void OnTranscriptActivityTick(object? sender, EventArgs e) => RenderTranscript();

    private void RenderTranscript()
    {
        // Bail before serializing anything: while the page is down the stale flag must survive so
        // the next successful navigation re-pushes whatever changed in the meantime.
        if (!_transcriptReady || _disposed)
        {
            return;
        }

        // Records the paint that gates ScheduleTranscriptRender's maximum wait.
        _lastRenderAt = DateTimeOffset.UtcNow;

        // The page keeps a per-message signature and reuses unchanged DOM, so re-sending an
        // identical payload is pure waste - and the messages array carries every attached image's
        // full base64 payload (up to 5 x 5 MB per message, ~33 MB encoded). Serialize the messages
        // only when a change notification says they moved.
        bool mustPush = _messagesJsonStale || _messagesJson is null;
        if (mustPush)
        {
            _messagesJson = JsonConvert.SerializeObject(_viewModel.Messages.Select(message => new
            {
                role = message.Role.ToString(),
                // Ordered so text and tool calls interleave exactly as the agent emitted them,
                // rather than "all text, then all tool calls" (message.Text/.ToolCalls group by
                // kind and lose that order - see ChatMessagePart).
                parts = message.Parts.Select(part => BuildPartPayload(part, message.Role)),
                durationSeconds = message.DurationSeconds,
                tokensUsed = message.TokensUsed,
                pending = message.IsPending,
                images = new JRaw(ImagesJson(message)),
            }));
            _messagesJsonStale = false;
        }

        string activityJson = JsonConvert.SerializeObject(_busyStartedAt is DateTimeOffset startedAt
            ? new
            {
                text = _viewModel.ActivityText,
                elapsedSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds),
                tokens = _viewModel.TurnTokens,
            }
            : null);
        if (!mustPush)
        {
            if (activityJson == _activityJson)
            {
                return;
            }

            // The activity block's elapsed-seconds counter changes once a second for the whole
            // turn. Re-posting render() for it would re-transmit _messagesJson - every attachment's
            // base64 included - as a fresh string plus its BSTR marshal on the UI thread, once a
            // second. setActivity() touches only the indicator and leaves the messages DOM alone.
            if (PostToTranscript($"window.claudeTranscript.setActivity({activityJson});"))
            {
                _activityJson = activityJson;
            }

            return;
        }

        // Newtonsoft's output is interpolated as a JS expression: safe on the evergreen Chromium
        // WebView2 runtime, where ES2019 legalised raw U+2028/U+2029 inside string literals.
        if (PostToTranscript($"window.claudeTranscript.render({{\"messages\":{_messagesJson},\"activity\":{activityJson}}});"))
        {
            _activityJson = activityJson;
        }
    }

    // A browser-process crash or an Edge Evergreen update under a running devenv invalidates
    // CoreWebView2, after which ExecuteScriptAsync throws at the COM boundary. Two of the three
    // callers run from a DispatcherTimer tick and from a DependencyProperty callback, where an
    // escaping exception is an unhandled dispatcher exception - a devenv crash, not a blank panel.
    // Fail closed instead: drop the ready latch. ProcessFailed decides whether the document is
    // recoverable and re-navigates if it is; that navigation completing is what re-latches it.
    private bool PostToTranscript(string script)
    {
        if (!_transcriptReady || _disposed || TranscriptView.CoreWebView2 is null)
        {
            return false;
        }

        try
        {
            _ = TranscriptView.CoreWebView2.ExecuteScriptAsync(script);
            return true;
        }
        catch (Exception exception) when (exception is COMException || exception is InvalidOperationException ||
                                          exception is ObjectDisposedException)
        {
            InvalidateTranscriptPage();
            return false;
        }
    }

    private void InvalidateTranscriptPage()
    {
        _transcriptReady = false;
        // The page's own render cache dies with it, so the next load must re-push everything.
        _messagesJson = null;
        _activityJson = null;
        _messagesJsonStale = true;
    }

    // Recovery for the one failure that actually loses the document: navigate back to the page.
    // NavigationCompleted then re-latches _transcriptReady and re-pushes everything, because
    // InvalidateTranscriptPage has already dropped the payload caches. lostMessage is what the
    // user is told if this reload is not attempted or cannot be issued; it stays on screen, since
    // the panel is dead for the rest of the session and the composer still accepts prompts.
    private void ReloadTranscriptPage(string lostMessage)
    {
        InvalidateTranscriptPage();
        if (_transcriptReloadAttempted || TranscriptView.CoreWebView2 is not CoreWebView2 core)
        {
            ShowCopyFeedback(lostMessage, persistent: true);
            return;
        }

        _transcriptReloadAttempted = true;
        try
        {
            core.Navigate(TranscriptHostProtocol.PageUrl);
        }
        catch (Exception exception) when (exception is COMException || exception is InvalidOperationException ||
                                          exception is ObjectDisposedException)
        {
            ShowCopyFeedback(lostMessage, persistent: true);
        }
    }

    private string ImagesJson(ChatMessageViewModel message)
    {
        if (!_imagesJsonByMessage.TryGetValue(message, out string? json))
        {
            json = JsonConvert.SerializeObject(message.Images.Select(image =>
                new { name = image.Name, mimeType = image.MimeType, data = image.Base64Data }));
            _imagesJsonByMessage[message] = json;
        }

        return json;
    }

    private static object BuildPartPayload(ChatMessagePart part, ChatRole role)
    {
        if (part is ChatTextPart textPart)
        {
            // Only the assistant's text is linkified. The page renders a user bubble as plain text
            // (renderUserText writes the lines into textContent) and scans those same raw lines for
            // pasted-diff headers, so rewriting a path the user typed would echo the markdown
            // source back at its author and hand the header patterns a rewritten path. Issue #24 is
            // about references in Claude's responses, which are the only ones rendered as markdown.
            return new
            {
                type = "text",
                text = role == ChatRole.Assistant ? ChatFileReference.LinkifyFileReferences(textPart.Text) : textPart.Text,
            };
        }

        var call = ((ChatToolCallPart)part).Card;
        return new
        {
            type = "tool",
            id = call.ToolCallId,
            title = call.Title,
            status = call.Status.ToString(),
            content = call.Content.Select(content => new
            {
                isDiff = content.IsDiff,
                text = content.Text,
                path = content.Path,
                diffLines = content.IsDiff
                    ? content.DiffLines.Select(line => new { kind = line.Kind.ToString(), prefix = line.Prefix, text = line.Text })
                    : null,
            }),
        };
    }

    private void PushFontSize() => PostToTranscript(
        $"window.claudeTranscript.setFontSize({ChatTextFontSize.ToString(CultureInfo.InvariantCulture)});");

    private void PushTheme()
    {
        var vars = new Dictionary<string, string>
        {
            ["--chat-bg"] = ResourceBrushToCss("ChatBackgroundBrush"),
            ["--chat-fg"] = ResourceBrushToCss("ChatForegroundBrush"),
            ["--chat-subtle-fg"] = ResourceBrushToCss("ChatSubtleForegroundBrush"),
            ["--chat-border"] = ResourceBrushToCss("ChatBorderBrush"),
            ["--chat-user-bubble-bg"] = ResourceBrushToCss("ChatUserBubbleBackgroundBrush"),
            ["--chat-input-bg"] = ResourceBrushToCss("ChatInputBackgroundBrush"),
            ["--chat-accent"] = ResourceBrushToCss("ChatAccentBrush"),
            ["--chat-accent-fg"] = ResourceBrushToCss("ChatAccentForegroundBrush"),
            ["--chat-link"] = ResourceBrushToCss("ChatLinkBrush"),
            ["--chat-diff-added-bg"] = ResourceBrushToCss("ChatDiffAddedBackgroundBrush"),
            ["--chat-diff-added-fg"] = ResourceBrushToCss("ChatDiffAddedForegroundBrush"),
            ["--chat-diff-removed-bg"] = ResourceBrushToCss("ChatDiffRemovedBackgroundBrush"),
            ["--chat-diff-removed-fg"] = ResourceBrushToCss("ChatDiffRemovedForegroundBrush"),
            ["--chat-error-fg"] = ResourceBrushToCss("ChatErrorForegroundBrush"),
        };
        // Editor syntax colors are optional (host-provided); the page keeps its own palette otherwise.
        AddBrushIfPresent(vars, "--hljs-keyword", "ChatCodeKeywordBrush");
        AddBrushIfPresent(vars, "--hljs-string", "ChatCodeStringBrush");
        AddBrushIfPresent(vars, "--hljs-comment", "ChatCodeCommentBrush");
        AddBrushIfPresent(vars, "--hljs-number", "ChatCodeNumberBrush");
        AddBrushIfPresent(vars, "--hljs-type", "ChatCodeTypeBrush");
        AddBrushIfPresent(vars, "--hljs-identifier", "ChatCodeIdentifierBrush");
        AddBrushIfPresent(vars, "--hljs-attribute", "ChatCodeAttributeBrush");

        _ = PostToTranscript($"window.claudeTranscript.applyTheme({JsonConvert.SerializeObject(vars)});");
    }

    /// <summary>Called by the host (ChatToolWindowPane) after it applies new VS theme colors onto
    /// this control's Resources, since WPF's DynamicResource updates don't reach JS on their own.</summary>
    public void RefreshTranscriptTheme() => PushTheme();

    private void AddBrushIfPresent(System.Collections.Generic.Dictionary<string, string> vars, string cssVariable, string resourceKey)
    {
        if (TryFindResource(resourceKey) is SolidColorBrush)
        {
            vars[cssVariable] = ResourceBrushToCss(resourceKey);
        }
    }

    private string ResourceBrushToCss(string resourceKey)
    {
        if (TryFindResource(resourceKey) is SolidColorBrush brush)
        {
            Color color = brush.Color;
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
        }

        return "inherit";
    }

    private void OnTranscriptWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // The page renders untrusted agent-authored markdown, so the envelope is untrusted too -
        // and only the transcript document itself may drive the host at all.
        if (_disposed || !TranscriptHostProtocol.IsTranscriptOrigin(e.Source))
        {
            return;
        }

        JObject message;
        try
        {
            message = JObject.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        // Read through JValue rather than JToken.Value<T>(): Value<T>() throws InvalidCastException
        // for a container token and FormatException/OverflowException for an unconvertible scalar,
        // neither of which is a JsonException. Escaping here means an unhandled dispatcher exception
        // on devenv's UI thread. Note `?.` only guards a missing key - a JSON null is JValue.Null.
        switch (ReadString(message, "type"))
        {
            case "openLink":
                OpenTranscriptLink(ReadString(message, "url"));
                break;
            case "openFile":
                // OpenFileReferenceAsync never faults (see its doc comment) - it re-parses and
                // re-validates href itself via ChatFileReference.TryParseLink rather than trusting
                // this message, and reports failure through StatusMessage instead of throwing, so
                // a bare discard cannot leak an unobserved exception onto this COM callback thread.
                _ = _viewModel.OpenFileReferenceAsync(ReadString(message, "href"));
                break;
            case "zoom":
                // DOM deltaY>0 is "scroll down" (zoom out); WPF's Ctrl+wheel convention is the
                // opposite sign (positive Delta = zoom in) - negate to match.
                ChatTextFontSize = TranscriptHostProtocol.StepFontSize(
                    ChatTextFontSize, -ReadDouble(message, "delta"));
                break;
        }
    }

    private static string? ReadString(JObject message, string property) =>
        (message[property] as JValue)?.Value as string;

    private static double ReadDouble(JObject message, string property) =>
        (message[property] as JValue)?.Value is IConvertible value && value is not string
            ? SafeToDouble(value)
            : 0d;

    private static double SafeToDouble(IConvertible value)
    {
        try
        {
            return value.ToDouble(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException || exception is FormatException ||
                                          exception is OverflowException)
        {
            return 0d;
        }
    }

    private void OpenTranscriptLink(string? target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || !MarkdownSafetyLimits.IsNavigableLink(uri))
        {
            ShowCopyFeedback("This link cannot be opened. Only absolute HTTP and HTTPS links are allowed.");
            return;
        }

        try
        {
            using (Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }))
            {
            }
        }
        catch (Exception exception) when (exception is Win32Exception || exception is InvalidOperationException ||
                                          exception is SecurityException || exception is ArgumentException)
        {
            ShowCopyFeedback("Could not open the link in your browser: " + exception.Message);
        }
    }

    // Docking/reparenting can unload WPF views; only the owning host ends the session.
    private void OnUnloaded(object sender, RoutedEventArgs e) => CloseAllPopups();

    // Every Popup here is a separate top-level window (AllowsTransparency="True"), and
    // StaysOpen="False" only dismisses on an outside *click* - which an unload is not. Left open,
    // one becomes an orphaned borderless window floating over the IDE, detached from a live visual
    // tree. HistoryPopup/UsagePopup bind IsOpen OneWay, so they must be closed through the view
    // model (see HistoryCloseButton_Click): assigning IsOpen directly would replace the binding
    // with a local value and the popup could never be reopened.
    private void CloseAllPopups()
    {
        ModelPopup.IsOpen = false;
        ModePopup.IsOpen = false;
        _viewModel.CloseHistory();
        _viewModel.IsUsagePanelOpen = false;
        _viewModel.DismissSlashSuggestions();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseAllPopups();
        Unloaded -= OnUnloaded;
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Tick -= OnCopyFeedbackTimerTick;
        _transcriptRenderTimer.Stop();
        _transcriptRenderTimer.Tick -= OnTranscriptActivityTick;
        _transcriptRenderDebounceTimer.Stop();
        _transcriptRenderDebounceTimer.Tick -= OnTranscriptRenderDebounceTick;
        _viewModel.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PlanReviewRequested -= OnPlanReviewRequested;
        StopTrackingTranscriptItems();
        if (TranscriptView.CoreWebView2 is CoreWebView2 core)
        {
            core.WebMessageReceived -= OnTranscriptWebMessageReceived;
            core.NavigationStarting -= OnTranscriptNavigationStarting;
            core.NavigationCompleted -= OnTranscriptNavigationCompleted;
            core.NewWindowRequested -= OnTranscriptNewWindowRequested;
            core.ProcessFailed -= OnTranscriptProcessFailed;
        }

        CommandManager.RemovePreviewCanExecuteHandler(ComposerBox, ComposerBox_PreviewCanExecute);
        CommandManager.RemovePreviewExecutedHandler(ComposerBox, ComposerBox_PreviewExecuted);
        _viewModel.Dispose();
        TranscriptView.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ComposerBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && TryPasteImage())
        {
            e.Handled = true;
            return;
        }

        if (HandleSlashKey(e))
        {
            return;
        }

        // Leave Shift+Enter and IME composition to the native TextBox.
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.SendCommand.CanExecute(null))
        {
            ClearAttachmentError();
            _viewModel.SendCommand.Execute(null);
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => ComposerBox.Focus();

    private void ComposerBox_PreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !CanEditDraft)
        {
            return;
        }

        try
        {
            if (!Clipboard.ContainsText() && Clipboard.ContainsImage())
            {
                e.CanExecute = true;
                e.Handled = true;
            }
        }
        catch (ExternalException)
        {
            // Clipboard ownership can change during a command-status query. The actual paste reports errors.
        }
    }

    private void ComposerBox_PreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Paste && TryPasteImage())
        {
            e.Handled = true;
        }
    }

    private bool CanEditDraft => !_disposed && !_viewModel.NeedsAuthentication && !_viewModel.IsBusy &&
        !_viewModel.IsConfigBusy && !_viewModel.IsConnecting;

    private bool TryPasteImage()
    {
        if (!CanEditDraft)
        {
            return false;
        }

        try
        {
            // Text retains native Unicode, selection replacement, undo and multiline semantics.
            if (Clipboard.ContainsText() || !Clipboard.ContainsImage())
            {
                return false;
            }

            var bitmap = Clipboard.GetImage();
            if (bitmap == null)
            {
                ShowAttachmentError("The clipboard image is unavailable. Copy it again and retry.");
            }
            else
            {
                AddImage(bitmap, "Pasted image.png");
            }
        }
        catch (Exception ex) when (IsImageInputError(ex))
        {
            ShowAttachmentError("Could not read that clipboard image. Copy it again or attach an image file.");
        }

        return true;
    }

    private void AttachImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditDraft)
        {
            return;
        }

        var picker = new OpenFileDialog
        {
            Title = "Attach an image",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff",
            CheckFileExists = true,
            Multiselect = false,
        };

        try
        {
            var owner = Window.GetWindow(this);
            if ((owner == null ? picker.ShowDialog() : picker.ShowDialog(owner)) != true)
            {
                return;
            }

            using (var stream = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaxImageBytes)
                {
                    ShowAttachmentError("Choose an image smaller than 5 MB.");
                    return;
                }

                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                AddImage(decoder.Frames[0], Path.GetFileName(picker.FileName));
            }
        }
        catch (Exception ex) when (IsImageInputError(ex))
        {
            ShowAttachmentError("Could not open that image. Choose a readable PNG, JPEG, GIF, BMP or TIFF file.");
        }
        finally
        {
            ComposerBox.Focus();
        }
    }

    private void AddImage(BitmapSource bitmap, string name)
    {
        if (_viewModel.Attachments.Count(attachment => attachment.IsImage) >= MaxImages)
        {
            ShowAttachmentError("Attach up to 5 images per message. Remove an image to add another.");
            return;
        }

        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaxImagePixels)
        {
            ShowAttachmentError("That image is too large. Resize it to 20 megapixels or fewer.");
            return;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var encoded = new MemoryStream())
        {
            encoder.Save(encoded);
            if (encoded.Length > MaxImageBytes)
            {
                ShowAttachmentError("The image exceeds 5 MB as PNG. Resize it and try again.");
                return;
            }

            _viewModel.AddImageAttachment(name, "image/png", Convert.ToBase64String(encoded.GetBuffer(), 0, (int)encoded.Length));
        }

        ClearAttachmentError();
    }

    private static bool IsImageInputError(Exception exception) => exception is IOException ||
        exception is UnauthorizedAccessException || exception is SecurityException ||
        exception is ExternalException || exception is NotSupportedException ||
        exception is ArgumentException || exception is InvalidOperationException || exception is FileFormatException;

    private void ShowAttachmentError(string message) => _viewModel.AttachmentError = message;

    private void ClearAttachmentError() => _viewModel.AttachmentError = null;

    // A persistent notice outlives the timer: a later transient notice may replace it on screen,
    // but when that one times out the persistent text comes back instead of the line collapsing.
    // Used for the transcript-lost states, which last for the rest of the panel's life while the
    // composer keeps accepting prompts whose replies would never appear.
    private void ShowCopyFeedback(string message, bool persistent = false)
    {
        if (persistent)
        {
            _persistentFeedback = message;
        }

        CopyFeedback.Text = message;
        CopyFeedback.Visibility = Visibility.Visible;
        var peer = UIElementAutomationPeer.FromElement(CopyFeedback) ??
            UIElementAutomationPeer.CreatePeerForElement(CopyFeedback);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _copyFeedbackTimer.Stop();
        if (!persistent)
        {
            _copyFeedbackTimer.Start();
        }
    }

    private void OnCopyFeedbackTimerTick(object? sender, EventArgs e)
    {
        _copyFeedbackTimer.Stop();
        if (_persistentFeedback is string persistent)
        {
            CopyFeedback.Text = persistent;
            return;
        }

        CopyFeedback.Visibility = Visibility.Collapsed;
    }

    private bool HandleSlashKey(KeyEventArgs e)
    {
        if (!_viewModel.AreSlashSuggestionsVisible || Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.DismissSlashSuggestions();
                ComposerBox.Focus();
                break;
            case Key.Up:
            case Key.Down:
                if (_viewModel.SlashSuggestions.Count > 0)
                {
                    var current = _viewModel.SelectedSlashSuggestion == null ? -1 :
                        _viewModel.SlashSuggestions.IndexOf(_viewModel.SelectedSlashSuggestion);
                    var next = current < 0 ? 0 : Math.Max(0, Math.Min(
                        _viewModel.SlashSuggestions.Count - 1, current + (e.Key == Key.Down ? 1 : -1)));
                    _viewModel.SelectedSlashSuggestion = _viewModel.SlashSuggestions[next];
                    SlashList.ScrollIntoView(_viewModel.SelectedSlashSuggestion);
                }
                break;
            case Key.Enter:
            case Key.Tab:
                // Nothing was actually selected/applicable: let the key fall through to its normal
                // behavior (e.g. inserting a newline or moving focus) instead of swallowing it.
                if (!AcceptSlashSuggestion())
                {
                    return false;
                }
                break;
            default:
                return false;
        }

        e.Handled = true;
        return true;
    }

    private bool AcceptSlashSuggestion()
    {
        var command = _viewModel.SelectedSlashSuggestion;
        if (!CanEditDraft || command == null || !_viewModel.ApplySlashSuggestionCommand.CanExecute(command))
        {
            return false;
        }

        _viewModel.ApplySlashSuggestionCommand.Execute(command);
        ComposerBox.Focus();
        ComposerBox.CaretIndex = ComposerBox.Text.Length;
        return true;
    }

    private void SlashList_PreviewKeyDown(object sender, KeyEventArgs e) => HandleSlashKey(e);

    private void SlashList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(SlashList, source) is ListBoxItem item &&
            item.DataContext is AvailableCommand command)
        {
            _viewModel.SelectedSlashSuggestion = command;
            AcceptSlashSuggestion();
            e.Handled = true;
        }
    }

    // The Popups in this view all set AllowsTransparency="True", which makes each one a separate
    // top-level window: they paint above the WebView2 transcript's native child HWND on their own,
    // so no airspace workaround (hiding TranscriptView while a popup is open) is needed.
    private void SlashPopup_Closed(object sender, EventArgs e) => _viewModel.DismissSlashSuggestions();

    private void ModelButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DismissSlashSuggestions();
        ModelPopup.IsOpen = !ModelPopup.IsOpen;
    }

    private void ModelPopup_Opened(object sender, EventArgs e)
    {
        ModelPickerView.Visibility = Visibility.Visible;
        EffortPickerView.Visibility = Visibility.Collapsed;
        ModelList.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
        EffortList.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
        FocusConfigList(ModelList, ModelPopup);
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DismissSlashSuggestions();
        ModePopup.IsOpen = !ModePopup.IsOpen;
    }

    private void ModePopup_Opened(object sender, EventArgs e)
    {
        ModeList.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
        FocusConfigList(ModeList, ModePopup);
    }

    private void FocusConfigList(ListBox list, Popup owner)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!owner.IsOpen || !list.IsVisible)
            {
                return;
            }

            var selected = list.SelectedItem == null ? null :
                list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as ListBoxItem;
            if (selected != null)
            {
                selected.Focus();
            }
            else
            {
                list.Focus();
            }
        }));
    }

    private void EffortButton_Click(object sender, RoutedEventArgs e) => ShowEffortOptions();

    private void ShowEffortOptions()
    {
        if (!_viewModel.HasEffort || !_viewModel.CanConfigure)
        {
            return;
        }

        ModelPickerView.Visibility = Visibility.Collapsed;
        EffortPickerView.Visibility = Visibility.Visible;
        EffortList.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
        FocusConfigList(EffortList, ModelPopup);
    }

    private void EffortBackButton_Click(object sender, RoutedEventArgs e) => ShowModelOptions();

    private void ShowModelOptions()
    {
        EffortPickerView.Visibility = Visibility.Collapsed;
        ModelPickerView.Visibility = Visibility.Visible;
        ModelList.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
        EffortButton.Focus();
    }

    private void ModelPopup_Closed(object sender, EventArgs e)
    {
        if (!_disposed && (ModelPickerView.IsKeyboardFocusWithin || EffortPickerView.IsKeyboardFocusWithin))
        {
            ModelButton.Focus();
        }
    }

    private void AttachmentErrorDismiss_Click(object sender, RoutedEventArgs e) => _viewModel.DismissAttachmentError();

    private void RemoteControlLink_Click(object sender, RoutedEventArgs e)
    {
        // RemoteControlUrl is agent-reported, so https only: claude.ai/code always is, and http
        // would let a hostile agent point this at a plaintext endpoint. OpenTranscriptLink owns the
        // rest - IsNavigableLink, disposing the Process, a filtered catch, and telling the user when
        // the launch fails (the tooltip only ever showed the URL, never the failure).
        if (TranscriptHostProtocol.NormalizeRemoteControlLink(_viewModel.RemoteControlUrl) is string link)
        {
            OpenTranscriptLink(link);
            return;
        }

        ShowCopyFeedback("This session link cannot be opened. Only absolute HTTPS links are allowed.");
    }

    private void SlashButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy || _viewModel.IsConnecting || _viewModel.IsConfigBusy || _viewModel.NeedsAuthentication)
        {
            return;
        }

        _viewModel.InputText = "/";
        ComposerBox.Focus();
        ComposerBox.CaretIndex = ComposerBox.Text.Length;
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DismissSlashSuggestions();
        if (HistoryPopup.IsOpen)
        {
            _viewModel.CloseHistory();
        }
        else
        {
            _ = _viewModel.ShowHistoryAsync();
        }
    }

    // Not HistoryPopup.IsOpen = false: that property is data-bound to IsHistoryOpen
    // (Mode=OneWay), and setting it directly here would replace the binding with a local
    // value, permanently severing it - the popup could never be reopened afterward. Go
    // through the view model, same as the Popup's own Closed handler below.
    private void HistoryCloseButton_Click(object sender, RoutedEventArgs e) => _viewModel.CloseHistory();

    private void HistoryPopup_Opened(object sender, EventArgs e) => HistoryFilterBox.Focus();

    private void HistoryPopup_Closed(object sender, EventArgs e)
    {
        _viewModel.CloseHistory();
        if (!_disposed && HistoryList.IsKeyboardFocusWithin)
        {
            HistoryButton.Focus();
        }
    }

    private void UsageButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DismissSlashSuggestions();
        _viewModel.IsUsagePanelOpen = !_viewModel.IsUsagePanelOpen;
    }

    private void UsageCloseButton_Click(object sender, RoutedEventArgs e) => _viewModel.IsUsagePanelOpen = false;

    private void UsagePopup_Closed(object sender, EventArgs e)
    {
        _viewModel.IsUsagePanelOpen = false;
        if (!_disposed && UsagePopupBorder.IsKeyboardFocusWithin)
        {
            UsageButton.Focus();
        }
    }

    private void UsageWarningLink_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DismissSlashSuggestions();
        _viewModel.IsUsagePanelOpen = true;
    }

    private void UsageWarningDismiss_Click(object sender, RoutedEventArgs e) => _viewModel.DismissUsageWarning();

    private void ModelPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Escape || e.Key == Key.Left) && EffortPickerView.Visibility == Visibility.Visible)
        {
            ShowModelOptions();
            e.Handled = true;
        }
        else if (e.Key == Key.Right && EffortButton.IsKeyboardFocusWithin)
        {
            ShowEffortOptions();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ModelPopup.IsOpen = false;
            ModelButton.Focus();
            e.Handled = true;
        }
    }

    private void ConfigList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(list, source) is ListBoxItem)
        {
            CommitConfigSelection(list);
            e.Handled = true;
        }
    }

    private void ConfigList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            CommitConfigSelection((ListBox)sender);
            e.Handled = true;
        }
    }

    private void CommitConfigSelection(ListBox list)
    {
        if (!_viewModel.CanConfigure || !(list.SelectedItem is SessionConfigValue value))
        {
            return;
        }

        if (ReferenceEquals(list, ModeList))
        {
            ModePopup.IsOpen = false;
            ModeButton.Focus();
            _viewModel.SelectedMode = value;
            return;
        }

        ModelPopup.IsOpen = false;
        ModelButton.Focus();
        if (ReferenceEquals(list, ModelList))
        {
            _viewModel.SelectedModel = value;
        }
        else
        {
            _viewModel.SelectedEffort = value;
        }
    }
}

/// <summary>Preserves the transcript's relative typography as its base text size changes. The
/// arithmetic lives in <see cref="TranscriptHostProtocol.ScaleFontSize"/> so it is reachable from
/// the XAML-free test host; this type is only the WPF binding adapter.</summary>
public sealed class ChatTextFontSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        TranscriptHostProtocol.ScaleFontSize(
            (double)value, System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
