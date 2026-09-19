using ClaudeCode.Core.ViewModels;
using ClaudeCode.Core.Views;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeCode.Vsix;

[Guid(PackageGuids.ChatToolWindowPersistanceString)]
public sealed class ChatToolWindowPane : ToolWindowPane
{
    private readonly ChatPanelView _view;
    private bool _disposed;

    public ChatToolWindowPane() : base(null)
    {
        Caption = "Claude Code";
        // TODO(imagemanifest-missing, Low/cosmetic): this GUID/ID moniker is only auto-registered with
        // the VS image service when used from the VSCT-compiled command table (see the OpenChatWindow
        // <Button><Icon> in ClaudeCode.vsct). Whether it also resolves correctly here, assigned directly
        // to a ToolWindowPane's tab bitmap outside any VSCT <Button>, needs live-VS visual verification;
        // if it does not, switch to BitmapResourceID/BitmapIndex (legacy VSCT strip addressing) or add a
        // full .imagemanifest. Left as-is: no observed rendering defect, and both alternatives are a
        // larger change than this cosmetic finding warrants without a live repro.
        BitmapImageMoniker = new ImageMoniker { Guid = PackageGuids.ClaudeCodeImages, Id = 1 };
        _view = new ChatPanelView();
        ApplyTheme();
        LoadBrandImage();
        VSColorTheme.ThemeChanged += OnThemeChanged;
        _view.PlanReviewRequested += OnPlanReviewRequested;
        _notifier = new VsAttentionNotifier();
        _view.AttentionRequested += OnAttentionRequested;
        Content = _view;
    }

    private readonly VsAttentionNotifier _notifier;

    private void OnAttentionRequested(object? sender, ChatAttentionEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // the view model raises this on its UI SynchronizationContext
        if (_disposed || Package is not ClaudeCodePackage package || !package.GetOptions().NotifyWhenInBackground) return;
        try
        {
            _notifier.Notify(e.Title, e.Message);
        }
        catch (Exception exception)
        {
            ActivityLog.TryLogError("Claude Code", "Could not show the notification: " + exception);
        }
    }

    private void LoadBrandImage()
    {
        // Reuse the packaged extension identity; Core has no VS SDK or asset dependency.
        var path = Path.Combine(Path.GetDirectoryName(typeof(ChatToolWindowPane).Assembly.Location)!, "Resources", "Icon.png");
        if (!File.Exists(path))
        {
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        _view.Resources["ChatBrandImage"] = image;
    }

    private void OnThemeChanged(ThemeChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ApplyTheme();
            });
        }
        catch (Exception exception)
        {
            ActivityLog.TryLogError("Claude Code", "Could not refresh the chat theme: " + exception);
        }
    }

    private void ApplyTheme()
    {
        if (_disposed)
        {
            return;
        }

        VsChatTheme.Apply(_view);
        // The transcript's WebView2 page can't see WPF's DynamicResource updates above on its own.
        _view.RefreshTranscriptTheme();
    }

    private void OnPlanReviewRequested(object? sender, PlanReviewViewModel plan)
    {
        if (_disposed || Package is not ClaudeCodePackage package) return;
        package.JoinableTaskFactory.RunAsync(async () =>
        {
            try { await package.ShowPlanAsync(plan); }
            catch (Exception exception) { ActivityLog.TryLogError("Claude Code", "Could not open the plan window: " + exception); }
        }).FileAndForget("claudecode/showplan");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            VSColorTheme.ThemeChanged -= OnThemeChanged;
            _view.PlanReviewRequested -= OnPlanReviewRequested;
            _view.AttentionRequested -= OnAttentionRequested;
            _notifier.Dispose();
            _view.Dispose();
        }

        base.Dispose(disposing);
    }
}
