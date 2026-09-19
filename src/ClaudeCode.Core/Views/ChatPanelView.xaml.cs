using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using ClaudeCode.Core.ViewModels.Demo;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
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
    internal const double DefaultChatTextFontSize = 13d;
    private const int CopyFeedbackDisplayMilliseconds = 4000;

    public static readonly DependencyProperty ChatTextFontSizeProperty = DependencyProperty.Register(
        nameof(ChatTextFontSize), typeof(double), typeof(ChatPanelView),
        new FrameworkPropertyMetadata(DefaultChatTextFontSize, OnChatTextFontSizeChanged));

    public static Func<IChatSessionServices>? ServicesFactory { get; set; }

    private readonly ChatViewModel _viewModel;
    private readonly DispatcherTimer _copyFeedbackTimer;
    private readonly DispatcherTimer _transcriptRenderTimer;
    private readonly DispatcherTimer _transcriptRenderDebounceTimer;
    private bool _disposed;
    private bool _transcriptReady;
    private DateTimeOffset? _busyStartedAt;

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
        // Message text and tool-call content/status only ever change while IsBusy is true (an
        // in-flight assistant turn) - so polling the transcript at a short interval exactly while
        // busy, plus an immediate render on any Messages add/remove/reset, captures every change
        // without wiring a change listener onto every message/tool-call/content item individually.
        _transcriptRenderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _transcriptRenderTimer.Tick += (_, __) => RenderTranscript();
        // Session-resume replays every past message as its own Messages.Add - rendering the whole
        // transcript synchronously on each one is O(n^2) work for an n-message history and was
        // visibly slow. Debounce instead: a burst of adds arriving within one tick collapses into a
        // single render once the burst goes quiet.
        _transcriptRenderDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _transcriptRenderDebounceTimer.Tick += (_, __) =>
        {
            _transcriptRenderDebounceTimer.Stop();
            RenderTranscript();
        };
        _viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.PlanReviewRequested += OnPlanReviewRequested;
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
        ChatTextFontSize = Math.Max(10d, Math.Min(28d, ChatTextFontSize + Math.Sign(e.Delta)));
    }

    private async Task InitializeTranscriptWebViewAsync()
    {
        try
        {
            // A dedicated profile directory: WebView2 refuses to share one with another running
            // instance, and this extension has no reason to touch the user's own Edge profile/history.
            string userDataFolder = Path.Combine(Path.GetTempPath(), "ClaudeCodeVsWebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            await TranscriptView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most likely cause: the WebView2 Runtime isn't installed on this machine. The transcript
            // area simply stays blank rather than crashing the whole tool window over it.
            return;
        }

        if (_disposed)
        {
            return;
        }

        CoreWebView2 core = TranscriptView.CoreWebView2;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Deny: nothing outside this page's own origin may load resources through this mapping.
        core.SetVirtualHostNameToFolderMapping(
            "claudecode.transcript", GetTranscriptAssetsPath(), CoreWebView2HostResourceAccessKind.Deny);
        core.WebMessageReceived += OnTranscriptWebMessageReceived;
        // Sanitized, local-only content never legitimately opens a new window/tab; block it outright.
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationCompleted += OnTranscriptNavigationCompleted;
        TranscriptView.Source = new Uri("https://claudecode.transcript/index.html");
    }

    private static string GetTranscriptAssetsPath() => Path.Combine(
        Path.GetDirectoryName(typeof(ChatPanelView).Assembly.Location) ?? string.Empty, "Resources", "Transcript");

    private void OnTranscriptNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed || !e.IsSuccess)
        {
            return;
        }

        _transcriptReady = true;
        PushTheme();
        PushFontSize();
        RenderTranscript();
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _transcriptRenderDebounceTimer.Stop();
        _transcriptRenderDebounceTimer.Start();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatViewModel.IsBusy))
        {
            return;
        }

        if (_viewModel.IsBusy)
        {
            _busyStartedAt = DateTimeOffset.UtcNow;
            _transcriptRenderTimer.Start();
            RenderTranscript(); // show the activity indicator immediately, don't wait for the first tick
        }
        else
        {
            _busyStartedAt = null;
            _transcriptRenderTimer.Stop();
            RenderTranscript(); // final flush so the last streamed chunk/tool update isn't missed
        }
    }

    private void RenderTranscript()
    {
        if (!_transcriptReady || _disposed)
        {
            return;
        }

        var payload = new
        {
            messages = _viewModel.Messages.Select(message => new
            {
                role = message.Role.ToString(),
                // Ordered so text and tool calls interleave exactly as the agent emitted them,
                // rather than "all text, then all tool calls" (message.Text/.ToolCalls group by
                // kind and lose that order - see ChatMessagePart).
                parts = message.Parts.Select(BuildPartPayload),
                durationSeconds = message.DurationSeconds,
                tokensUsed = message.TokensUsed,
                images = message.Images.Select(image => new { name = image.Name, mimeType = image.MimeType, data = image.Base64Data }),
            }),
            activity = _busyStartedAt is DateTimeOffset startedAt
                ? new
                {
                    text = _viewModel.ActivityText,
                    elapsedSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds),
                    tokens = _viewModel.TurnTokens,
                }
                : null,
        };

        string json = JsonConvert.SerializeObject(payload);
        _ = TranscriptView.CoreWebView2.ExecuteScriptAsync($"window.claudeTranscript.render({json});");
    }

    private static object BuildPartPayload(ChatMessagePart part)
    {
        if (part is ChatTextPart textPart)
        {
            return new { type = "text", text = textPart.Text };
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

    private void PushFontSize()
    {
        if (!_transcriptReady || _disposed)
        {
            return;
        }

        _ = TranscriptView.CoreWebView2.ExecuteScriptAsync(
            $"window.claudeTranscript.setFontSize({ChatTextFontSize.ToString(CultureInfo.InvariantCulture)});");
    }

    private void PushTheme()
    {
        if (!_transcriptReady || _disposed)
        {
            return;
        }

        var vars = new System.Collections.Generic.Dictionary<string, string>
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

        string json = JsonConvert.SerializeObject(vars);
        _ = TranscriptView.CoreWebView2.ExecuteScriptAsync($"window.claudeTranscript.applyTheme({json});");
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
        if (_disposed)
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

        switch (message["type"]?.Value<string>())
        {
            case "openLink":
                OpenTranscriptLink(message["url"]?.Value<string>());
                break;
            case "zoom":
                // JS wheel deltaY>0 is "scroll down" (zoom out); WPF's own Ctrl+wheel convention is
                // the opposite sign (wheel-up/positive Delta = zoom in) - negate to match.
                double delta = message["delta"]?.Value<double>() ?? 0;
                ChatTextFontSize = Math.Max(10d, Math.Min(28d, ChatTextFontSize - Math.Sign(delta)));
                break;
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
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ModelPopup.IsOpen = false;
        _viewModel.DismissSlashSuggestions();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unloaded -= OnUnloaded;
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Tick -= OnCopyFeedbackTimerTick;
        _transcriptRenderTimer.Stop();
        _transcriptRenderDebounceTimer.Stop();
        _viewModel.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PlanReviewRequested -= OnPlanReviewRequested;
        if (TranscriptView.CoreWebView2 is not null)
        {
            TranscriptView.CoreWebView2.WebMessageReceived -= OnTranscriptWebMessageReceived;
        }

        ModelPopup.IsOpen = false;
        _viewModel.DismissSlashSuggestions();
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

    private void ShowCopyFeedback(string message)
    {
        CopyFeedback.Text = message;
        CopyFeedback.Visibility = Visibility.Visible;
        var peer = UIElementAutomationPeer.FromElement(CopyFeedback) ??
            UIElementAutomationPeer.CreatePeerForElement(CopyFeedback);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private void OnCopyFeedbackTimerTick(object? sender, EventArgs e)
    {
        _copyFeedbackTimer.Stop();
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

    // WebView2 hosts a native child HWND that always paints on top of ordinary WPF content
    // ("airspace") - including these Popups, which open above the composer and so land right over
    // the transcript area. Hiding TranscriptView for as long as any popup is open is the standard
    // workaround; a counter (rather than a bool) tolerates one popup opening before another finishes
    // closing instead of the second Closed prematurely revealing the WebView2 mid-way through.
    private int _openPopupCount;

    // Popups are separate top-level windows, so they paint above the WebView2 child HWND on their
    // own; the transcript stays visible while they are open. The counter is kept so a future
    // popup-aware behavior (e.g. pausing transcript re-renders) has a single place to hook.
    private void OnAnyPopupOpened() => _openPopupCount++;

    private void OnAnyPopupClosed() => _openPopupCount = Math.Max(0, _openPopupCount - 1);

    private void SlashPopup_Opened(object sender, EventArgs e) => OnAnyPopupOpened();

    private void SlashPopup_Closed(object sender, EventArgs e)
    {
        OnAnyPopupClosed();
        _viewModel.DismissSlashSuggestions();
    }

    private void ModelButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DismissSlashSuggestions();
        ModelPopup.IsOpen = !ModelPopup.IsOpen;
    }

    private void ModelPopup_Opened(object sender, EventArgs e)
    {
        OnAnyPopupOpened();
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
        OnAnyPopupOpened();
        ModeList.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
        FocusConfigList(ModeList, ModePopup);
    }

    private void ModePopup_Closed(object sender, EventArgs e) => OnAnyPopupClosed();

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
        OnAnyPopupClosed();
        if (!_disposed && (ModelPickerView.IsKeyboardFocusWithin || EffortPickerView.IsKeyboardFocusWithin))
        {
            ModelButton.Focus();
        }
    }

    private void AttachmentErrorDismiss_Click(object sender, RoutedEventArgs e) => _viewModel.DismissAttachmentError();

    private void RemoteControlLink_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_viewModel.RemoteControlUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // No browser/handler available: the tooltip still shows the URL to copy.
            }
        }
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

    private void HistoryPopup_Opened(object sender, EventArgs e)
    {
        OnAnyPopupOpened();
        HistoryFilterBox.Focus();
    }

    private void HistoryPopup_Closed(object sender, EventArgs e)
    {
        OnAnyPopupClosed();
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

    private void UsagePopup_Opened(object sender, EventArgs e) => OnAnyPopupOpened();

    private void UsagePopup_Closed(object sender, EventArgs e)
    {
        OnAnyPopupClosed();
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

/// <summary>Preserves the transcript's relative typography as its base text size changes.</summary>
public sealed class ChatTextFontSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (double)value * System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture) /
        ChatPanelView.DefaultChatTextFontSize;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
