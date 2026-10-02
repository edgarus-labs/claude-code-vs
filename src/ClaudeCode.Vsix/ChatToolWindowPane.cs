using ClaudeCode.Core.ViewModels;
using ClaudeCode.Core.Views;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace ClaudeCode.Vsix;

[Guid(PackageGuids.ChatToolWindowPersistanceString)]
public sealed class ChatToolWindowPane : ToolWindowPane
{
    private readonly ChatPanelView _view;
    private readonly VsAttentionNotifier _notifier;
    private readonly Microsoft.VisualStudio.Text.Classification.IClassificationFormatMap? _editorFormatMap;
    private bool _disposed;

    public ChatToolWindowPane() : base(null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = "Claude Code";
        BitmapImageMoniker = new ImageMoniker { Guid = PackageGuids.ClaudeCodeImages, Id = 1 };
        _view = new ChatPanelView();
        ApplyTheme();
        LoadBrandImage();
        _notifier = new VsAttentionNotifier(ActivateChatWindow);
        _view.PlanReviewRequested += OnPlanReviewRequested;
        _view.AttentionRequested += OnAttentionRequested;
        Content = _view;
        _editorFormatMap = VsChatTheme.TryGetEditorFormatMap();
        VSColorTheme.ThemeChanged += OnThemeChanged;
        if (_editorFormatMap is not null)
        {
            _editorFormatMap.ClassificationFormatMappingChanged += OnClassificationFormatMappingChanged;
        }
    }

    private void ActivateChatWindow()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed || Frame is not IVsWindowFrame frame) return;
        ErrorHandler.ThrowOnFailure(frame.Show());
    }

    private void OnAttentionRequested(object? sender, ChatAttentionEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed) return;
        try
        {
            if (Package is not ClaudeCodePackage package || !package.GetOptions().NotifyWhenInBackground) return;
            _notifier.Notify(e.Title, e.Message);
        }
        catch (Exception exception)
        {
            ActivityLog.TryLogError("Claude Code", "Could not show the notification: " + exception);
        }
    }

    private void LoadBrandImage()
    {
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

    private void OnThemeChanged(ThemeChangedEventArgs e) => RefreshTheme();

    private void OnClassificationFormatMappingChanged(object sender, EventArgs e) => RefreshTheme();

    private void RefreshTheme()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            ThreadHelper.JoinableTaskFactory.Run(RefreshThemeAsync);
        }
        catch (Exception exception)
        {
            ActivityLog.TryLogError("Claude Code", "Could not refresh the chat theme: " + exception);
        }
    }

    private async System.Threading.Tasks.Task RefreshThemeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed)
        {
            return;
        }

        VsChatTheme.Apply(_view);
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
            if (_editorFormatMap is not null)
            {
                _editorFormatMap.ClassificationFormatMappingChanged -= OnClassificationFormatMappingChanged;
            }

            _view.PlanReviewRequested -= OnPlanReviewRequested;
            _view.AttentionRequested -= OnAttentionRequested;
            _notifier.Dispose();
            _view.Dispose();
        }

        base.Dispose(disposing);
    }
}
