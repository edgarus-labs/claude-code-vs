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
    /// <summary>
    /// The plan lost message.
    /// </summary>
    private const string _planLostMessage = "The plan viewer stopped working. Close this window and open the plan again.";

    private readonly DispatcherTimer _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
    private PlanReviewViewModel? _plan;
    private bool _ready;
    private bool _disposed;
    private ulong? _cancelledNavigationId;
    private bool _reloadAttempted;
    private string? _persistentNotice;

    public PlanDocumentView()
    {
        InitializeComponent();
        _noticeTimer.Tick += OnNoticeTimerTick;
        _ = InitializeWebViewAsync();
    }

    /// <summary>
    /// Gets or sets the plan.
    /// </summary>
    public PlanReviewViewModel? Plan
    {
        get => _plan;
        set
        {
            if (_plan is not null)
            {
                _plan.PropertyChanged -= OnPlanPropertyChanged;
            }

            _plan = value;
            DataContext = value;
            if (_plan is not null)
            {
                _plan.PropertyChanged += OnPlanPropertyChanged;
            }

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
            if (_disposed)
            {
                return;
            }

            await PlanView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            CoreWebView2 core = PlanView.CoreWebView2;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.SetVirtualHostNameToFolderMapping("claudecode.plan", GetAssetsPath(), CoreWebView2HostResourceAccessKind.Deny);
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
            if (!_disposed)
            {
                ShowNotice("The plan could not be displayed. The Microsoft Edge WebView2 Runtime is required: " + ex.Message, persistent: true);
            }
        }
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (PlanDocumentProtocol.IsPlanDocumentUri(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        _cancelledNavigationId = e.NavigationId;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
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

            ReloadPlanPage();
            return;
        }

        _cancelledNavigationId = null;
        _reloadAttempted = false;
        _ready = true;
        PushTheme();
        Render();
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                ReloadPlanPage();
                break;

            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                _ready = false;
                ShowNotice(_planLostMessage, persistent: true);
                break;
        }
    }

    private void ReloadPlanPage()
    {
        _ready = false;
        if (_reloadAttempted || PlanView.CoreWebView2 is not CoreWebView2 core)
        {
            ShowNotice(_planLostMessage, persistent: true);
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
            ShowNotice(_planLostMessage, persistent: true);
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

    private void PostToPlan(string script)
    {
        if (!_ready || _disposed || PlanView.CoreWebView2 is null)
        {
            return;
        }

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
        if (_disposed || !PlanDocumentProtocol.IsPlanDocumentUri(e.Source))
        {
            return;
        }

        string? url;
        try
        {
            var message = JsonConvert.DeserializeAnonymousType(e.WebMessageAsJson, new { type = "", url = "" });
            if (message?.type != "openLink")
            {
                return;
            }

            url = message.url;
        }
        catch (Exception)
        {
            return;
        }

        OpenPlanLink(url);
    }

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
        if (ReviewPanel.Visibility == Visibility.Visible)
        {
            ReviewBox.Focus();
        }
    }

    private void ReviewCancel_Click(object sender, RoutedEventArgs e) => ReviewPanel.Visibility = Visibility.Collapsed;

    private void ReviewSend_Click(object sender, RoutedEventArgs e)
    {
        var comments = ReviewBox.Text;
        if (_plan is null || string.IsNullOrWhiteSpace(comments))
        {
            return;
        }

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

    private void ShowNotice(string message, bool persistent = false)
    {
        if (_disposed)
        {
            return;
        }

        if (persistent)
        {
            _persistentNotice = message;
        }

        PlanNotice.Text = message;
        PlanNotice.Visibility = Visibility.Visible;
        var peer = UIElementAutomationPeer.FromElement(PlanNotice) ?? UIElementAutomationPeer.CreatePeerForElement(PlanNotice);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _noticeTimer.Stop();
        if (!persistent)
        {
            _noticeTimer.Start();
        }
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _noticeTimer.Stop();
        _noticeTimer.Tick -= OnNoticeTimerTick;
        if (_plan is not null)
        {
            _plan.PropertyChanged -= OnPlanPropertyChanged;
        }

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
