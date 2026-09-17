using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Wpf;
using Markdig.Renderers.Wpf.Inlines;
using Markdig.Syntax.Inlines;
using Markdig.Wpf;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClaudeCode.Core.Views;

/// <summary>A selectable, read-only Markdown document sized by the transcript.</summary>
public sealed class MarkdownMessageView : RichTextBox
{
    private static readonly ChatTextFontSizeConverter FontSizeConverter = new ChatTextFontSizeConverter();

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownMessageView),
        new FrameworkPropertyMetadata(string.Empty, OnMarkdownChanged));

    public static readonly RoutedEvent FeedbackEvent = EventManager.RegisterRoutedEvent(
        nameof(Feedback), RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<string>),
        typeof(MarkdownMessageView));

    private readonly DispatcherTimer _renderTimer;
    private string? _renderedMarkdown;

    public MarkdownMessageView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsUndoEnabled = false;
        AcceptsTab = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(ForegroundProperty, "ChatForegroundBrush");
        SetResourceReference(SelectionBrushProperty, "ChatSelectionBrush");
        InstallDocumentStyles();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _renderTimer.Tick += OnRenderTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public event RoutedPropertyChangedEventHandler<string> Feedback
    {
        add => AddHandler(FeedbackEvent, value);
        remove => RemoveHandler(FeedbackEvent, value);
    }

    protected override Size MeasureOverride(Size constraint) =>
        base.MeasureOverride(new Size(constraint.Width, double.PositiveInfinity));

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        // The transcript owns vertical scrolling, including while text is selected.
        for (DependencyObject? ancestor = VisualTreeHelper.GetParent(this);
             ancestor != null;
             ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is ScrollViewer scrollViewer)
            {
                e.Handled = true;
                scrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = MouseWheelEvent,
                    Source = this
                });
                return;
            }
        }

        base.OnPreviewMouseWheel(e);
    }

    private static void OnMarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownMessageView)sender).ScheduleRender();

    private void ScheduleRender()
    {
        // A hidden assistant template also exists for user messages: never parse it.
        if (IsLoaded && IsVisible && !_renderTimer.IsEnabled)
        {
            _renderTimer.Start();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RenderLatest();

    private void OnUnloaded(object sender, RoutedEventArgs e) => _renderTimer.Stop();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ScheduleRender();
        }
        else
        {
            _renderTimer.Stop();
        }
    }

    private void OnRenderTick(object? sender, EventArgs e) => RenderLatest();

    private void RenderLatest()
    {
        // One-shot throttle: an idle or unloaded message never keeps a timer alive.
        _renderTimer.Stop();
        if (!IsLoaded || !IsVisible)
        {
            return;
        }

        var markdown = Markdown ?? string.Empty;
        if (string.Equals(markdown, _renderedMarkdown, StringComparison.Ordinal))
        {
            return;
        }

        var preserveSelection = !Selection.IsEmpty || IsKeyboardFocusWithin;
        var selectionStart = preserveSelection ? Document.ContentStart.GetOffsetToPosition(Selection.Start) : 0;
        var selectionEnd = preserveSelection ? Document.ContentStart.GetOffsetToPosition(Selection.End) : 0;
        Document = Markdig.Wpf.Markdown.ToFlowDocument(markdown, Pipeline, new SafeWpfRenderer(this));
        _renderedMarkdown = markdown;

        if (preserveSelection)
        {
            var length = Document.ContentStart.GetOffsetToPosition(Document.ContentEnd);
            var start = Document.ContentStart.GetPositionAtOffset(Math.Min(selectionStart, length));
            var end = Document.ContentStart.GetPositionAtOffset(Math.Min(selectionEnd, length));
            if (start != null && end != null)
            {
                Selection.Select(start, end);
            }
        }
    }

    private Hyperlink CreateHyperlink(string? target)
    {
        // Deliberately omit NavigateUri and Commands.Hyperlink: only our click handler
        // may invoke the shell, after checking the actual destination again.
        var hyperlink = new Hyperlink { Tag = target, ToolTip = target ?? "Missing link destination" };
        hyperlink.SetResourceReference(FrameworkContentElement.StyleProperty, Styles.HyperlinkStyleKey);
        hyperlink.Click += OnHyperlinkClick;
        return hyperlink;
    }

    private void OnHyperlinkClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var hyperlink = (Hyperlink)sender;
        var target = hyperlink.Tag as string;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrEmpty(uri.Host))
        {
            ReportLinkFailure(hyperlink, "This link cannot be opened. Only absolute HTTP and HTTPS links are allowed.");
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
            ReportLinkFailure(hyperlink, "Could not open the link in your browser: " + exception.Message);
        }
    }

    private void ReportLinkFailure(Hyperlink hyperlink, string message)
    {
        hyperlink.ToolTip = message + Environment.NewLine + (hyperlink.Tag as string ?? string.Empty);
        RaiseEvent(new RoutedPropertyChangedEventArgs<string>(string.Empty, message, FeedbackEvent));
    }

    private void InstallDocumentStyles()
    {
        AddStyle(Styles.DocumentStyleKey, typeof(FlowDocument),
            new Setter(TextElement.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this }),
            new Setter(FlowDocument.PagePaddingProperty, new Thickness(0)),
            new Setter(TextElement.ForegroundProperty, BrushResource("ChatForegroundBrush")));
        AddStyle(Styles.ParagraphStyleKey, typeof(Paragraph),
            new Setter(Block.MarginProperty, new Thickness(0, 0, 0, 8)));

        var headingKeys = new object[]
        {
            Styles.Heading1StyleKey, Styles.Heading2StyleKey, Styles.Heading3StyleKey,
            Styles.Heading4StyleKey, Styles.Heading5StyleKey, Styles.Heading6StyleKey
        };
        var headingSizes = new[] { 22d, 20d, 18d, 16d, 14d, 13d };
        for (var index = 0; index < headingKeys.Length; index++)
        {
            AddStyle(headingKeys[index], typeof(Paragraph),
                new Setter(TextElement.FontSizeProperty, new Binding(nameof(FontSize))
                {
                    Source = this,
                    Converter = FontSizeConverter,
                    ConverterParameter = headingSizes[index]
                }),
                new Setter(TextElement.FontWeightProperty, FontWeights.SemiBold),
                new Setter(Block.MarginProperty, new Thickness(0, 10, 0, 6)),
                new Setter(Paragraph.KeepWithNextProperty, true));
        }

        var codeFont = new FontFamily("Consolas");
        AddStyle(Styles.CodeStyleKey, typeof(Run),
            new Setter(TextElement.FontFamilyProperty, codeFont),
            new Setter(TextElement.BackgroundProperty, BrushResource("ChatInputBackgroundBrush")));
        AddStyle(Styles.CodeBlockStyleKey, typeof(Paragraph),
            new Setter(TextElement.FontFamilyProperty, codeFont),
            new Setter(TextElement.BackgroundProperty, BrushResource("ChatInputBackgroundBrush")),
            new Setter(Block.BorderBrushProperty, BrushResource("ChatBorderBrush")),
            new Setter(Block.BorderThicknessProperty, new Thickness(1)),
            new Setter(Block.PaddingProperty, new Thickness(10)),
            new Setter(Block.MarginProperty, new Thickness(0, 4, 0, 10)));
        AddStyle(Styles.HyperlinkStyleKey, typeof(Hyperlink),
            new Setter(TextElement.ForegroundProperty, BrushResource("ChatAccentBrush")),
            new Setter(System.Windows.Documents.Inline.TextDecorationsProperty, TextDecorations.Underline));
        AddStyle(Styles.QuoteBlockStyleKey, typeof(Section),
            new Setter(TextElement.ForegroundProperty, BrushResource("ChatSubtleForegroundBrush")),
            new Setter(Block.BorderBrushProperty, BrushResource("ChatBorderBrush")),
            new Setter(Block.BorderThicknessProperty, new Thickness(3, 0, 0, 0)),
            new Setter(Block.PaddingProperty, new Thickness(10, 0, 0, 0)),
            new Setter(Block.MarginProperty, new Thickness(0, 4, 0, 8)));
        AddStyle(Styles.TableStyleKey, typeof(Table),
            new Setter(Table.CellSpacingProperty, 0d),
            new Setter(Block.MarginProperty, new Thickness(0, 4, 0, 10)));
        AddStyle(Styles.TableCellStyleKey, typeof(TableCell),
            new Setter(TableCell.BorderBrushProperty, BrushResource("ChatBorderBrush")),
            new Setter(TableCell.BorderThicknessProperty, new Thickness(0, 0, 0, 1)),
            new Setter(TableCell.PaddingProperty, new Thickness(8, 5, 8, 5)));
        AddStyle(Styles.TableHeaderStyleKey, typeof(TableRow),
            new Setter(TextElement.FontWeightProperty, FontWeights.SemiBold),
            new Setter(TextElement.BackgroundProperty, BrushResource("ChatInputBackgroundBrush")));
        AddStyle(Styles.ThematicBreakStyleKey, typeof(Line),
            new Setter(Shape.StrokeProperty, BrushResource("ChatBorderBrush")),
            new Setter(Shape.StrokeThicknessProperty, 1d),
            new Setter(Shape.StretchProperty, Stretch.Fill),
            new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
    }

    private static DynamicResourceExtension BrushResource(string key) => new DynamicResourceExtension(key);

    private void AddStyle(object key, Type targetType, params Setter[] setters)
    {
        var style = new Style(targetType);
        foreach (var setter in setters)
        {
            style.Setters.Add(setter);
        }
        Resources.Add(key, style);
    }

    private sealed class SafeWpfRenderer : WpfRenderer
    {
        private readonly MarkdownMessageView _owner;

        public SafeWpfRenderer(MarkdownMessageView owner) => _owner = owner;

        protected override void LoadRenderers()
        {
            base.LoadRenderers();
            // Replace BEFORE parsing/rendering: the default image renderer eagerly
            // creates BitmapImage instances (including remote and local resources).
            ObjectRenderers.Replace<LinkInlineRenderer>(new SafeLinkRenderer(_owner));
            ObjectRenderers.Replace<AutolinkInlineRenderer>(new SafeAutolinkRenderer(_owner));
        }
    }

    private sealed class SafeLinkRenderer : WpfObjectRenderer<LinkInline>
    {
        private readonly MarkdownMessageView _owner;

        public SafeLinkRenderer(MarkdownMessageView owner) => _owner = owner;

        protected override void Write(WpfRenderer renderer, LinkInline link)
        {
            var target = link.GetDynamicUrl != null ? link.GetDynamicUrl() ?? link.Url : link.Url;
            if (link.IsImage)
            {
                var description = new Span { ToolTip = "Image not loaded: " + target };
                description.SetResourceReference(TextElement.ForegroundProperty, "ChatSubtleForegroundBrush");
                renderer.Push(description);
                renderer.WriteText(link.FirstChild == null ? "[Image omitted]" : "[Image: ");
                if (link.FirstChild != null)
                {
                    renderer.WriteChildren(link);
                    renderer.WriteText("]");
                }
                renderer.Pop();
                return;
            }

            renderer.Push(_owner.CreateHyperlink(target));
            renderer.WriteChildren(link);
            renderer.Pop();
        }
    }

    private sealed class SafeAutolinkRenderer : WpfObjectRenderer<AutolinkInline>
    {
        private readonly MarkdownMessageView _owner;

        public SafeAutolinkRenderer(MarkdownMessageView owner) => _owner = owner;

        protected override void Write(WpfRenderer renderer, AutolinkInline link)
        {
            renderer.Push(_owner.CreateHyperlink(link.IsEmail ? "mailto:" + link.Url : link.Url));
            renderer.WriteText(link.Url);
            renderer.Pop();
        }
    }
}
