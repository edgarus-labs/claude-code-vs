using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using ClaudeCode.Core.ViewModels.Demo;
using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
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
        new FrameworkPropertyMetadata(DefaultChatTextFontSize));

    public static Func<IChatSessionServices>? ServicesFactory { get; set; }

    private readonly ChatViewModel _viewModel;
    private readonly DispatcherTimer _copyFeedbackTimer;
    private bool _disposed;
    private bool _isAtBottom = true;

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
        Unloaded += OnUnloaded;
    }

    public double ChatTextFontSize
    {
        get => (double)GetValue(ChatTextFontSizeProperty);
        private set => SetValue(ChatTextFontSizeProperty, value);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.Control || e.Delta == 0)
        {
            return;
        }

        // Intercept before any transcript/composer child scrolls or reroutes the wheel.
        // Even at a limit, keep Ctrl+wheel inside this pane rather than zooming its host.
        e.Handled = true;
        ChatTextFontSize = Math.Max(10d, Math.Min(28d, ChatTextFontSize + Math.Sign(e.Delta)));
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
        ModelPopup.IsOpen = false;
        _viewModel.DismissSlashSuggestions();
        CommandManager.RemovePreviewCanExecuteHandler(ComposerBox, ComposerBox_PreviewCanExecute);
        CommandManager.RemovePreviewExecutedHandler(ComposerBox, ComposerBox_PreviewExecuted);
        _viewModel.Dispose();
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

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (!(sender is FrameworkElement element) || !(element.DataContext is ChatMessageViewModel message))
        {
            return;
        }

        if (string.IsNullOrEmpty(message.Text))
        {
            ShowCopyFeedback("This message has no text to copy yet.");
            return;
        }

        try
        {
            Clipboard.SetText(message.Text);
            ShowCopyFeedback("Message copied.");
        }
        catch (Exception ex) when (ex is ExternalException || ex is SecurityException)
        {
            ShowCopyFeedback("Could not copy the message. The clipboard may be busy; try again.");
        }
    }

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

    private void MarkdownMessage_Feedback(object sender, RoutedPropertyChangedEventArgs<string> e) =>
        ShowCopyFeedback(e.NewValue);

    private void TranscriptScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var wasAtBottom = _isAtBottom;
        _isAtBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1;
        if (ChatTranscriptScrollPolicy.ShouldAutoScroll(wasAtBottom, e.ExtentHeightChange))
        {
            ((ScrollViewer)sender).ScrollToEnd();
        }
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

    private void HistoryPopup_Closed(object sender, EventArgs e)
    {
        _viewModel.CloseHistory();
        if (!_disposed && HistoryList.IsKeyboardFocusWithin)
        {
            HistoryButton.Focus();
        }
    }

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
