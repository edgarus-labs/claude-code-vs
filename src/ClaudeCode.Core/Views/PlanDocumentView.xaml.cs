using ClaudeCode.Core.ViewModels;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeCode.Core.Views;

/// <summary>Document-style view of an implementation plan awaiting approval: rendered markdown with
/// Proceed / Review actions bound to a <see cref="PlanReviewViewModel"/>.</summary>
public partial class PlanDocumentView : UserControl, IDisposable
{
    private const string PlanDocumentUri = "https://claudecode.plan/plan.html";

    private readonly DispatcherTimer _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
    private PlanReviewViewModel? _plan;
    private bool _ready;
    private bool _disposed;

    public PlanDocumentView()
    {
        InitializeComponent();
        _noticeTimer.Tick += OnNoticeTimerTick;
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
            HideNotice();
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
            if (_disposed) return;

            CoreWebView2 core = PlanView.CoreWebView2;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // Ctrl+S/Ctrl+P/Ctrl+F/F5 belong to the IDE, not to Chromium, inside a tool window.
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            // Nothing is ever exposed via AddHostObjectToScript; don't leave the door that permits it open.
            core.Settings.AreHostObjectsAllowed = false;
            core.SetVirtualHostNameToFolderMapping("claudecode.plan", GetAssetsPath(), CoreWebView2HostResourceAccessKind.Deny);
            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnProcessFailed;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            // Host-side backstop: the plan document may only ever sit on its own virtual host, so a
            // future CSP relaxation in plan.html cannot turn agent markdown into a top-level navigation.
            core.NavigationStarting += (_, args) =>
                args.Cancel = !args.Uri.StartsWith(PlanDocumentUri, StringComparison.Ordinal);
            core.NavigationCompleted += (_, args) =>
            {
                if (_disposed) return;
                if (!args.IsSuccess)
                {
                    _ready = false; // never leave a stale "ready" latch behind a failed (re)navigation
                    return;
                }

                _ready = true;
                PushTheme();
                Render();
            };
            PlanView.Source = new Uri(PlanDocumentUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most likely causes: the WebView2 Runtime isn't installed, or the plan assets weren't
            // deployed next to this assembly (SetVirtualHostNameToFolderMapping rejects the relative
            // path GetAssetsPath falls back to). The header actions still work, but the body stays
            // blank - say so instead of leaving the user staring at nothing, because this task is
            // fire-and-forget and an escaping exception would be an unobserved, undiagnosable fault.
            if (!_disposed)
            {
                ShowNotice("The plan could not be displayed. The Microsoft Edge WebView2 Runtime is required: " + ex.Message);
            }
        }
    }

    // Only a dead renderer needs host action here: WebView2 restarts its GPU/utility processes by
    // itself and "unresponsive" resolves on its own, so latching the window off for those kinds would
    // blank a plan that was about to come back. Re-navigating is what restores the document, and
    // NavigationCompleted re-pushes the theme and the plan body.
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_disposed) return;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                _ready = false;
                PlanView.CoreWebView2.Reload();
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The whole CoreWebView2 is gone; there is nothing left on this control to re-navigate.
                _ready = false;
                ShowNotice("The plan viewer stopped working. Close this window and open the plan again.");
                break;
        }
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
        // The page renders untrusted agent-authored markdown, so the envelope is untrusted too - and
        // only the plan document itself, never some future subframe, may drive the host.
        if (_disposed || !e.Source.StartsWith(PlanDocumentUri, StringComparison.Ordinal)) return;

        string? url;
        try
        {
            var message = JsonConvert.DeserializeAnonymousType(e.WebMessageAsJson, new { type = "", url = "" });
            if (message?.type != "openLink") return;
            url = message.url;
        }
        catch (Exception)
        {
            return; // Malformed page message: ignore.
        }

        OpenPlanLink(url);
    }

    // Mirrors ChatPanelView.OpenTranscriptLink: the same JS-side code path must give the same answer.
    private void OpenPlanLink(string? target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || !MarkdownSafetyLimits.IsNavigableLink(uri))
        {
            ShowNotice("This link cannot be opened. Only absolute HTTP and HTTPS links are allowed.");
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
            ShowNotice("Could not open the link in your browser: " + exception.Message);
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
        // The agent controls the option list, so a plan can arrive with no reject option at all, and a
        // session reset resolves the plan underneath this window. Never swallow the typed comments.
        if (!_plan.ReviewCommand.CanExecute(comments))
        {
            ShowNotice(_plan.IsResolved
                ? "This plan is no longer active, so the comments were not sent."
                : "Claude did not offer a way to send this plan back for revision.");
            return;
        }

        _plan.ReviewCommand.Execute(comments);
        ReviewBox.Text = string.Empty;
        ReviewPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowNotice(string message)
    {
        if (_disposed) return; // never restart the timer after its Tick handler is gone
        PlanNotice.Text = message;
        PlanNotice.Visibility = Visibility.Visible;
        var peer = UIElementAutomationPeer.FromElement(PlanNotice) ?? UIElementAutomationPeer.CreatePeerForElement(PlanNotice);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void HideNotice()
    {
        _noticeTimer.Stop();
        PlanNotice.Visibility = Visibility.Collapsed;
    }

    private void OnNoticeTimerTick(object? sender, EventArgs e) => HideNotice();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _noticeTimer.Stop();
        _noticeTimer.Tick -= OnNoticeTimerTick;
        if (_plan is not null) _plan.PropertyChanged -= OnPlanPropertyChanged;
        if (PlanView.CoreWebView2 is not null)
        {
            PlanView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            PlanView.CoreWebView2.ProcessFailed -= OnProcessFailed;
        }

        PlanView.Dispose();
        GC.SuppressFinalize(this);
    }
}
