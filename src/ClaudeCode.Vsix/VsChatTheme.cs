using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System.Windows;
using System.Windows.Media;

namespace ClaudeCode.Vsix;

/// <summary>Maps the current Visual Studio theme onto the Core views' brush keys (direct entries on a
/// view's resources override its merged standalone fallback dictionary).</summary>
internal static class VsChatTheme
{
    public static void Apply(FrameworkElement view)
    {
        SetBrush(view, "ChatBackgroundBrush", EnvironmentColors.ToolWindowBackgroundColorKey);
        SetBrush(view, "ChatForegroundBrush", EnvironmentColors.ToolWindowTextColorKey);
        // Derived from the tool window text color (not ComboBoxBorder/SystemGrayText): those keys
        // can be near-invisible against the input background, and VS renders some of them differently
        // while the main window is inactive - the composer frame and placeholder then vanished.
        SetDerivedBrush(view, "ChatSubtleForegroundBrush", EnvironmentColors.ToolWindowTextColorKey, 0x99);
        SetDerivedBrush(view, "ChatBorderBrush", EnvironmentColors.ToolWindowTextColorKey, 0x48);
        SetBrush(view, "ChatInputBackgroundBrush", EnvironmentColors.ComboBoxBackgroundColorKey);
        SetBrush(view, "ChatPopupBackgroundBrush", EnvironmentColors.CommandBarMenuBackgroundGradientBeginColorKey);
        SetBrush(view, "ChatHoverBrush", ThemedDialogColors.ListItemMouseOverColorKey);
        SetBrush(view, "ChatHoverForegroundBrush", ThemedDialogColors.ListItemMouseOverTextColorKey);
        SetBrush(view, "ChatSelectionBrush", ThemedDialogColors.SelectedItemActiveColorKey);
        SetBrush(view, "ChatSelectionForegroundBrush", ThemedDialogColors.SelectedItemActiveTextColorKey);
        // Claude brand accent/focus colors are owned by Core, not the current VS accent.
        SetBrush(view, "ChatUserBubbleBackgroundBrush", ThemedDialogColors.SelectedItemInactiveColorKey);
        SetBrush(view, "ChatAssistantBubbleBackgroundBrush", EnvironmentColors.ToolWindowBackgroundColorKey);
        SetBrush(view, "ChatDiffAddedBackgroundBrush", ThemedDialogColors.SelectedItemInactiveColorKey);
        SetBrush(view, "ChatDiffAddedForegroundBrush", ThemedDialogColors.SelectedItemInactiveTextColorKey);
        SetBrush(view, "ChatDiffRemovedBackgroundBrush", EnvironmentColors.ToolWindowBackgroundColorKey);
        SetBrush(view, "ChatDiffRemovedForegroundBrush", EnvironmentColors.ToolWindowValidationErrorTextColorKey);
        SetBrush(view, "ChatErrorForegroundBrush", EnvironmentColors.ToolWindowValidationErrorTextColorKey);
        SetBrush(view, "ChatWarningBackgroundBrush", ThemedDialogColors.PromotionBoxBackgroundColorKey);
    }

    private static void SetDerivedBrush(FrameworkElement view, string key, ThemeResourceKey themeKey, byte alpha)
    {
        var color = VSColorTheme.GetThemedColor(themeKey);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        view.Resources[key] = brush;
    }

    private static void SetBrush(FrameworkElement view, string key, ThemeResourceKey themeKey)
    {
        var color = VSColorTheme.GetThemedColor(themeKey);
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        view.Resources[key] = brush;
    }
}
