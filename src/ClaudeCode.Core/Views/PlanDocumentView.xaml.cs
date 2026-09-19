using ClaudeCode.Core.ViewModels;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeCode.Core.Views;

/// <summary>Document-style view of an implementation plan awaiting approval: rendered markdown with
/// Proceed / Review actions bound to a <see cref="PlanReviewViewModel"/>.</summary>
public partial class PlanDocumentView : UserControl, IDisposable
{
    private PlanReviewViewModel? _plan;
    private bool _ready;
    private bool _disposed;

    public PlanDocumentView()
    {
        InitializeComponent();
        _ = InitializeWebViewAsync();
    }

    public PlanReviewViewModel? Plan
    {
        get => _plan;
        set
        {
            if (_plan is not null) _plan.PropertyChanged -= OnPlanPropertyChanged;
            _plan = value;
            DataContext = value;
            if (_plan is not null) _plan.PropertyChanged += OnPlanPropertyChanged;
            ReviewPanel.Visibility = Visibility.Collapsed;
            ReviewBox.Text = string.Empty;
            Render();
        }
    }

    private void OnPlanPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanReviewViewModel.IsResolved) && _plan?.IsResolved == true)
        {
            ReviewPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async System.Threading.Tasks.Task InitializeWebViewAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(Path.GetTempPath(), "ClaudeCodeVsWebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder).ConfigureAwait(true);
            if (_disposed) return;
            await PlanView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return; // No WebView2 runtime: the header actions still work, the body stays blank.
        }

        if (_disposed) return;
        CoreWebView2 core = PlanView.CoreWebView2;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("claudecode.plan", GetAssetsPath(), CoreWebView2HostResourceAccessKind.Deny);
        core.WebMessageReceived += OnWebMessageReceived;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationCompleted += (_, args) =>
        {
            if (_disposed || !args.IsSuccess) return;
            _ready = true;
            PushTheme();
            Render();
        };
        PlanView.Source = new Uri("https://claudecode.plan/plan.html");
    }

    private static string GetAssetsPath() => Path.Combine(
        Path.GetDirectoryName(typeof(PlanDocumentView).Assembly.Location) ?? string.Empty, "Resources", "Transcript");

    private void Render()
    {
        if (!_ready || _disposed) return;
        string json = JsonConvert.SerializeObject(_plan?.Markdown ?? string.Empty);
        _ = PlanView.CoreWebView2.ExecuteScriptAsync($"window.claudePlan.render({json});");
    }

    public void RefreshTheme() => PushTheme();

    private void PushTheme()
    {
        if (!_ready || _disposed) return;
        var vars = new
        {
            chatBg = Css("ChatBackgroundBrush"),
            chatFg = Css("ChatForegroundBrush"),
            chatSubtleFg = Css("ChatSubtleForegroundBrush"),
            chatBorder = Css("ChatBorderBrush"),
            chatInputBg = Css("ChatInputBackgroundBrush"),
            chatAccent = Css("ChatAccentBrush"),
            chatLink = Css("ChatLinkBrush"),
        };
        string json = JsonConvert.SerializeObject(new System.Collections.Generic.Dictionary<string, string>
        {
            ["--chat-bg"] = vars.chatBg,
            ["--chat-fg"] = vars.chatFg,
            ["--chat-subtle-fg"] = vars.chatSubtleFg,
            ["--chat-border"] = vars.chatBorder,
            ["--chat-input-bg"] = vars.chatInputBg,
            ["--chat-accent"] = vars.chatAccent,
            ["--chat-link"] = vars.chatLink,
        });
        _ = PlanView.CoreWebView2.ExecuteScriptAsync($"window.claudePlan.applyTheme({json});");
    }

    private string Css(string resourceKey)
    {
        if (TryFindResource(resourceKey) is SolidColorBrush brush)
        {
            Color color = brush.Color;
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
        }

        return "inherit";
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = JsonConvert.DeserializeAnonymousType(e.WebMessageAsJson, new { type = "", url = "" });
            if (message?.type == "openLink" && Uri.TryCreate(message.url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            // Malformed page message: ignore.
        }
    }

    private void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        ReviewPanel.Visibility = ReviewPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (ReviewPanel.Visibility == Visibility.Visible) ReviewBox.Focus();
    }

    private void ReviewCancel_Click(object sender, RoutedEventArgs e) => ReviewPanel.Visibility = Visibility.Collapsed;

    private void ReviewSend_Click(object sender, RoutedEventArgs e)
    {
        var comments = ReviewBox.Text;
        if (_plan is null || string.IsNullOrWhiteSpace(comments)) return;
        _plan.ReviewCommand.Execute(comments);
        ReviewBox.Text = string.Empty;
        ReviewPanel.Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_plan is not null) _plan.PropertyChanged -= OnPlanPropertyChanged;
        PlanView.Dispose();
        GC.SuppressFinalize(this);
    }
}
