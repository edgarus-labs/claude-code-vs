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
        Content = _view;
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

        // Direct entries on the view override its merged standalone fallback dictionary.
        SetBrush("ChatBackgroundBrush", EnvironmentColors.ToolWindowBackgroundColorKey);
        SetBrush("ChatForegroundBrush", EnvironmentColors.ToolWindowTextColorKey);
        SetBrush("ChatSubtleForegroundBrush", EnvironmentColors.SystemGrayTextColorKey);
        SetBrush("ChatBorderBrush", EnvironmentColors.ComboBoxBorderColorKey);
        SetBrush("ChatInputBackgroundBrush", EnvironmentColors.ComboBoxBackgroundColorKey);
        SetBrush("ChatPopupBackgroundBrush", EnvironmentColors.CommandBarMenuBackgroundGradientBeginColorKey);
        SetBrush("ChatHoverBrush", ThemedDialogColors.ListItemMouseOverColorKey);
        SetBrush("ChatHoverForegroundBrush", ThemedDialogColors.ListItemMouseOverTextColorKey);
        SetBrush("ChatSelectionBrush", ThemedDialogColors.SelectedItemActiveColorKey);
        SetBrush("ChatSelectionForegroundBrush", ThemedDialogColors.SelectedItemActiveTextColorKey);
        // Claude brand accent/focus colors are owned by Core, not the current VS accent.
        SetBrush("ChatUserBubbleBackgroundBrush", ThemedDialogColors.SelectedItemInactiveColorKey);
        SetBrush("ChatAssistantBubbleBackgroundBrush", EnvironmentColors.ToolWindowBackgroundColorKey);
        SetBrush("ChatDiffAddedBackgroundBrush", ThemedDialogColors.SelectedItemInactiveColorKey);
        SetBrush("ChatDiffAddedForegroundBrush", ThemedDialogColors.SelectedItemInactiveTextColorKey);
        SetBrush("ChatDiffRemovedBackgroundBrush", EnvironmentColors.ToolWindowBackgroundColorKey);
        SetBrush("ChatDiffRemovedForegroundBrush", EnvironmentColors.ToolWindowValidationErrorTextColorKey);
        SetBrush("ChatErrorForegroundBrush", EnvironmentColors.ToolWindowValidationErrorTextColorKey);
        SetBrush("ChatWarningBackgroundBrush", ThemedDialogColors.PromotionBoxBackgroundColorKey);
    }

    private void SetBrush(string key, ThemeResourceKey themeKey)
    {
        var color = VSColorTheme.GetThemedColor(themeKey);
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        _view.Resources[key] = brush;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            VSColorTheme.ThemeChanged -= OnThemeChanged;
            _view.Dispose();
        }

        base.Dispose(disposing);
    }
}
