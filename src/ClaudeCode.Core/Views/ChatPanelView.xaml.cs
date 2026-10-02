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
    private readonly Dictionary<ChatMessageViewModel, string> _imagesJsonByMessage =
        new Dictionary<ChatMessageViewModel, string>();
    private bool _disposed;
    private bool _transcriptReady;
    private DateTimeOffset? _busyStartedAt;
    private string? _messagesJson;
    private string? _activityJson;
    private bool _messagesJsonStale = true;
    private DateTimeOffset _lastRenderAt = DateTimeOffset.MinValue;
    private ulong? _cancelledNavigationId;
    private bool _transcriptReloadAttempted;
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
        _transcriptRenderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _transcriptRenderTimer.Tick += OnTranscriptActivityTick;
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

    /// <summary>Raised when Claude asks for approval of an implementation plan.</summary>
    public event EventHandler<PlanReviewViewModel>? PlanReviewRequested;

    /// <summary>Raised when the conversation needs the user's attention; forwards the view model's event.</summary>
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

    private void Popup_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => ApplyCtrlWheelZoom(e);

    private void ApplyCtrlWheelZoom(MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.Control || e.Delta == 0)
        {
            return;
        }

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

            TranscriptView.AllowExternalDrop = false;
            CoreWebView2 core = TranscriptView.CoreWebView2;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
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

    private void OnTranscriptNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) =>
        e.Handled = true;

    private void OnTranscriptNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (TranscriptHostProtocol.IsTranscriptOrigin(e.Uri) &&
            Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) &&
            string.Equals(uri.GetLeftPart(UriPartial.Path), TranscriptHostProtocol.PageUrl, StringComparison.Ordinal))
        {
            return;
        }

        e.Cancel = true;
        _cancelledNavigationId = e.NavigationId;
    }

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
                _cancelledNavigationId = null;
                return;
            }

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
            _imagesJsonByMessage.Clear();
        }

        TrackTranscriptItems();
        ScheduleTranscriptRender();
    }

    private void TrackTranscriptItems()
    {
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
        if (!_transcriptReady || _disposed)
        {
            return;
        }

        _lastRenderAt = DateTimeOffset.UtcNow;

        bool mustPush = _messagesJsonStale || _messagesJson is null;
        if (mustPush)
        {
            _messagesJson = JsonConvert.SerializeObject(_viewModel.Messages.Select(message => new
            {
                id = message.Id,
                role = message.Role.ToString(),
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

            if (PostToTranscript($"window.claudeTranscript.setActivity({activityJson});"))
            {
                _activityJson = activityJson;
            }

            return;
        }

        if (PostToTranscript($"window.claudeTranscript.render({{\"messages\":{_messagesJson},\"activity\":{activityJson}}});"))
        {
            _activityJson = activityJson;
        }
    }

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
        _messagesJson = null;
        _activityJson = null;
        _messagesJsonStale = true;
    }

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
            return new
            {
                type = "text",
                text = role == ChatRole.Assistant ? ChatFileReference.LinkifyFileReferences(textPart.Text) : textPart.Text,
            };
        }

        if (part is ChatThinkingPart thinkingPart)
        {
            return new { type = "thinking", text = thinkingPart.Text };
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
        AddBrushIfPresent(vars, "--hljs-keyword", "ChatCodeKeywordBrush");
        AddBrushIfPresent(vars, "--hljs-string", "ChatCodeStringBrush");
        AddBrushIfPresent(vars, "--hljs-comment", "ChatCodeCommentBrush");
        AddBrushIfPresent(vars, "--hljs-number", "ChatCodeNumberBrush");
        AddBrushIfPresent(vars, "--hljs-type", "ChatCodeTypeBrush");
        AddBrushIfPresent(vars, "--hljs-identifier", "ChatCodeIdentifierBrush");
        AddBrushIfPresent(vars, "--hljs-attribute", "ChatCodeAttributeBrush");

        _ = PostToTranscript($"window.claudeTranscript.applyTheme({JsonConvert.SerializeObject(vars)});");
    }

    /// <summary>Pushes the control's current theme colors to the transcript.</summary>
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

        switch (ReadString(message, "type"))
        {
            case "openLink":
                OpenTranscriptLink(ReadString(message, "url"));
                break;
            case "openFile":
                _ = _viewModel.OpenFileReferenceAsync(ReadString(message, "href"));
                break;
            case "zoom":
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

    private void OnUnloaded(object sender, RoutedEventArgs e) => CloseAllPopups();

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

    private string _composerTextSeen = string.Empty;
    private int _composerCaretSeen;

    private void ComposerBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        _composerTextSeen = ComposerBox.Text;
        _composerCaretSeen = ComposerBox.CaretIndex;
    }

    private void ComposerBox_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        var text = ComposerBox.Text;
        if (_composerTextSeen.Length > 0 && text.Length > _composerTextSeen.Length && text.EndsWith(_composerTextSeen, StringComparison.Ordinal))
        {
            ComposerBox.CaretIndex = text.Length - _composerTextSeen.Length + _composerCaretSeen;
        }
        _composerTextSeen = text;
        _composerCaretSeen = ComposerBox.CaretIndex;
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

        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && _viewModel.CancelCommand.CanExecute(null))
        {
            e.Handled = true;
            _viewModel.CancelCommand.Execute(null);
            return;
        }

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

/// <summary>Scales a transcript font size by the ratio in <c>ConverterParameter</c> using
/// <see cref="TranscriptHostProtocol.ScaleFontSize"/>.</summary>
public sealed class ChatTextFontSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        TranscriptHostProtocol.ScaleFontSize(
            (double)value, System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
