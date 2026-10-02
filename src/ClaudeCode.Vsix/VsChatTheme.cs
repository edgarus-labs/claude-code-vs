using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System;
using System.Windows;
using System.Windows.Media;

namespace ClaudeCode.Vsix;

internal static class VsChatTheme
{
    public static void Apply(FrameworkElement view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SetBrush(view, "ChatBackgroundBrush", EnvironmentColors.ToolWindowBackgroundColorKey);
        SetBrush(view, "ChatForegroundBrush", EnvironmentColors.ToolWindowTextColorKey);
        SetDerivedBrush(view, "ChatSubtleForegroundBrush", EnvironmentColors.ToolWindowTextColorKey, 0xCC);
        SetDerivedBrush(view, "ChatBorderBrush", EnvironmentColors.ToolWindowTextColorKey, 0x48);
        SetBrush(view, "ChatInputBackgroundBrush", EnvironmentColors.ComboBoxBackgroundColorKey);
        SetBrush(view, "ChatPopupBackgroundBrush", EnvironmentColors.CommandBarMenuBackgroundGradientBeginColorKey);
        SetBrush(view, "ChatHoverBrush", ThemedDialogColors.ListItemMouseOverColorKey);
        SetBrush(view, "ChatHoverForegroundBrush", ThemedDialogColors.ListItemMouseOverTextColorKey);
        SetBrush(view, "ChatSelectionBrush", ThemedDialogColors.SelectedItemActiveColorKey);
        SetBrush(view, "ChatSelectionForegroundBrush", ThemedDialogColors.SelectedItemActiveTextColorKey);
        SetBrush(view, "ChatUserBubbleBackgroundBrush", ThemedDialogColors.SelectedItemInactiveColorKey);
        SetFixedBrush(view, "ChatDiffAddedBackgroundBrush", 0x38, 0x2E, 0xA0, 0x43);
        SetFixedBrush(view, "ChatDiffAddedForegroundBrush", 0xFF, 0x3F, 0xB9, 0x50);
        SetFixedBrush(view, "ChatDiffRemovedBackgroundBrush", 0x38, 0xF8, 0x51, 0x49);
        SetFixedBrush(view, "ChatDiffRemovedForegroundBrush", 0xFF, 0xF8, 0x51, 0x49);
        ApplyEditorSyntaxColors(view);
        SetBrush(view, "ChatErrorForegroundBrush", EnvironmentColors.ToolWindowValidationErrorTextColorKey);
        SetBrush(view, "ChatWarningBackgroundBrush", ThemedDialogColors.PromotionBoxBackgroundColorKey);
    }

    private static readonly (string Key, string[] Classifications)[] _syntaxColorMap =
    {
        ("ChatCodeKeywordBrush", new[] { "keyword" }),
        ("ChatCodeStringBrush", new[] { "string" }),
        ("ChatCodeCommentBrush", new[] { "comment" }),
        ("ChatCodeNumberBrush", new[] { "number" }),
        ("ChatCodeTypeBrush", new[] { "class name", "type" }),
        ("ChatCodeIdentifierBrush", new[] { "identifier" }),
        ("ChatCodeAttributeBrush", new[] { "preprocessor keyword", "keyword" }),
    };

    public static Microsoft.VisualStudio.Text.Classification.IClassificationFormatMap? TryGetEditorFormatMap()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return GetComponentModel()?
                .GetService<Microsoft.VisualStudio.Text.Classification.IClassificationFormatMapService>()
                .GetClassificationFormatMap("text");
        }
        catch (Exception exception)
        {
            ActivityLog.TryLogWarning("Claude Code", "Editor format map unavailable: " + exception.Message);
            return null;
        }
    }

    private static Microsoft.VisualStudio.ComponentModelHost.IComponentModel? GetComponentModel()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ServiceProvider.GlobalProvider.GetService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel))
            as Microsoft.VisualStudio.ComponentModelHost.IComponentModel;
    }

    private static void ApplyEditorSyntaxColors(FrameworkElement view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (GetComponentModel() is not { } componentModel)
            {
                return;
            }

            var registry = componentModel.GetService<Microsoft.VisualStudio.Text.Classification.IClassificationTypeRegistryService>();
            var map = componentModel.GetService<Microsoft.VisualStudio.Text.Classification.IClassificationFormatMapService>().GetClassificationFormatMap("text");
            foreach (var (key, classifications) in _syntaxColorMap)
            {
                foreach (var name in classifications)
                {
                    var type = registry.GetClassificationType(name);
                    if (type is null) continue;
                    if (map.GetTextProperties(type).ForegroundBrush is SolidColorBrush brush && brush.Color.A > 0)
                    {
                        var frozen = new SolidColorBrush(brush.Color);
                        frozen.Freeze();
                        view.Resources[key] = frozen;
                        break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ActivityLog.TryLogWarning("Claude Code", "Editor syntax colors unavailable: " + exception.Message);
        }
    }

    private static void SetFixedBrush(FrameworkElement view, string key, byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        view.Resources[key] = brush;
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
