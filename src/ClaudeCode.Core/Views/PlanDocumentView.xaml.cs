using ClaudeCode.Core.ViewModels;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private const string PlanLostMessage = "The plan viewer stopped working. Close this window and open the plan again.";

    private readonly DispatcherTimer _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
    private PlanReviewViewModel? _plan;
    private bool _ready;
    private bool _disposed;
    // Set when NavigationStarting cancels an off-document navigation, so the NavigationCompleted
    // failure that cancel produces is not mistaken for the plan page itself failing to load.
    private ulong? _cancelledNavigationId;
    // One reload per successful load: if the reload we issued is itself what just failed, stop
    // rather than spinning navigate -> fail -> navigate on the UI thread.
    private bool _reloadAttempted;
    // The notice that must stay on screen for the rest of the window's life (see ShowNotice).
    private string? _persistentNotice;

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
            var environment = await CoreWebView2Environment.CreateAsync(null, WebView2Profile.UserDataFolder).ConfigureAwait(true);
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
            // A file or URL dropped onto the plan body would otherwise start a top-level navigation
            // away from the one document this view knows how to drive.
            PlanView.AllowExternalDrop = false;
            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnProcessFailed;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            PlanView.Source = new Uri(PlanDocumentProtocol.PageUrl);
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
                ShowNotice("The plan could not be displayed. The Microsoft Edge WebView2 Runtime is required: " + ex.Message, persistent: true);
            }
        }
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    // Host-side backstop: the plan document may only ever sit on its own virtual host, so a
    // future CSP relaxation in plan.html cannot turn agent markdown into a top-level navigation.
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Path-exact, not prefix: plan.html and index.html are served from the same mapped asset
        // folder, so a prefix check would let plan.html/../index.html navigate the frame to a
        // document this view does not drive (no window.claudePlan to render into).
        if (PlanDocumentProtocol.IsPlanDocumentUri(e.Uri)) return;

        // Cancelling leaves the plan document exactly where it was. Remember the id: the cancel
        // still raises NavigationCompleted with IsSuccess=false, and treating that as "the plan
        // page failed to load" dropped the ready latch for good - every later ShowPlan then swapped
        // the header's commands to the new plan while the body kept showing the previous one.
        e.Cancel = true;
        _cancelledNavigationId = e.NavigationId;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed) return;
        if (!e.IsSuccess)
        {
            if (e.NavigationId == _cancelledNavigationId)
            {
                // Our own guard cancelled this one; the plan document is still live.
                _cancelledNavigationId = null;
                return;
            }

            // Never leave a stale "ready" latch behind a failed load - and never leave the body
            // blank under a live Proceed/Review header without saying so.
            ReloadPlanPage();
            return;
        }

        _cancelledNavigationId = null;
        _reloadAttempted = false;
        _ready = true;
        PushTheme();
        Render();
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
                ReloadPlanPage();
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The whole CoreWebView2 is gone; there is nothing left on this control to re-navigate.
                _ready = false;
                ShowNotice(PlanLostMessage, persistent: true);
                break;
        }
    }

    // Recovery for the one failure that actually loses the document: navigate back to the page,
    // once. If that reload is what just failed, or cannot be issued, the window is dead for the
    // rest of its life and the notice stays up so an empty body is never mistaken for a plan.
    private void ReloadPlanPage()
    {
        _ready = false;
        if (_reloadAttempted || PlanView.CoreWebView2 is not CoreWebView2 core)
        {
            ShowNotice(PlanLostMessage, persistent: true);
            return;
        }

        _reloadAttempted = true;
        try
        {
            core.Navigate(PlanDocumentProtocol.PageUrl);
        }
        catch (Exception exception) when (exception is COMException || exception is InvalidOperationException ||
                                          exception is ObjectDisposedException)
        {
            ShowNotice(PlanLostMessage, persistent: true);
        }
    }

    private static string GetAssetsPath() => Path.Combine(
        Path.GetDirectoryName(typeof(PlanDocumentView).Assembly.Location) ?? string.Empty, "Resources", "Transcript");

    private void Render() =>
        PostToPlan($"window.claudePlan.render({JsonConvert.SerializeObject(_plan?.Markdown ?? string.Empty)});");

    public void RefreshTheme() => PushTheme();

    private void PushTheme()
    {
        string json = JsonConvert.SerializeObject(new System.Collections.Generic.Dictionary<string, string>
        {
            ["--chat-bg"] = Css("ChatBackgroundBrush"),
            ["--chat-fg"] = Css("ChatForegroundBrush"),
            ["--chat-subtle-fg"] = Css("ChatSubtleForegroundBrush"),
            ["--chat-border"] = Css("ChatBorderBrush"),
            ["--chat-input-bg"] = Css("ChatInputBackgroundBrush"),
            ["--chat-accent"] = Css("ChatAccentBrush"),
            ["--chat-link"] = Css("ChatLinkBrush"),
        });
        PostToPlan($"window.claudePlan.applyTheme({json});");
    }

    // A browser-process crash or an Edge Evergreen update under a running devenv invalidates
    // CoreWebView2, after which ExecuteScriptAsync throws at the COM boundary; RefreshTheme runs
    // from the host's theme-change handler, where an escaping exception would be an unhandled
    // dispatcher exception. Fail closed instead: drop the ready latch, and let ProcessFailed decide
    // whether the document is recoverable.
    private void PostToPlan(string script)
    {
        if (!_ready || _disposed || PlanView.CoreWebView2 is null) return;
        try
        {
            _ = PlanView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception exception) when (exception is COMException || exception is InvalidOperationException ||
                                          exception is ObjectDisposedException)
        {
            _ready = false;
        }
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
        if (_disposed || !PlanDocumentProtocol.IsPlanDocumentUri(e.Source)) return;

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

    // A persistent notice outlives the timer and the next ShowPlan: a later transient notice may
    // replace it on screen, but when that one times out - or a new plan hides it - the persistent
    // text comes back instead of the line collapsing. Used for the viewer-lost states, which last
    // for the rest of the window's life while the Proceed/Review header stays live.
    private void ShowNotice(string message, bool persistent = false)
    {
        if (_disposed) return; // never restart the timer after its Tick handler is gone
        if (persistent) _persistentNotice = message;
        PlanNotice.Text = message;
        PlanNotice.Visibility = Visibility.Visible;
        var peer = UIElementAutomationPeer.FromElement(PlanNotice) ?? UIElementAutomationPeer.CreatePeerForElement(PlanNotice);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _noticeTimer.Stop();
        if (!persistent) _noticeTimer.Start();
    }

    private void HideNotice()
    {
        _noticeTimer.Stop();
        if (_persistentNotice is string persistent)
        {
            PlanNotice.Text = persistent;
            return;
        }

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
        if (PlanView.CoreWebView2 is CoreWebView2 core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ProcessFailed -= OnProcessFailed;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
        }

        PlanView.Dispose();
        GC.SuppressFinalize(this);
    }
}
