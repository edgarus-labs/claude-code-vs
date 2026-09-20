using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System;
using System.Windows;
using System.Windows.Media;

namespace ClaudeCode.Vsix;

/// <summary>Maps the current Visual Studio theme onto the Core views' brush keys (direct entries on a
/// view's resources override its merged standalone fallback dictionary).</summary>
internal static class VsChatTheme
{
    public static void Apply(FrameworkElement view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SetBrush(view, "ChatBackgroundBrush", EnvironmentColors.ToolWindowBackgroundColorKey);
        SetBrush(view, "ChatForegroundBrush", EnvironmentColors.ToolWindowTextColorKey);
        // Derived from the tool window text color (not ComboBoxBorder/SystemGrayText): those keys
        // can be near-invisible against the input background, and VS renders some of them differently
        // while the main window is inactive - the composer frame and placeholder then vanished.
        // 0xCC (80% alpha) rather than the previous 0x99 (60%): timestamps, placeholders and
        // attachment names read as this brush too, and 60% fell below readable contrast on several
        // VS themes, especially on a 4K display.
        SetDerivedBrush(view, "ChatSubtleForegroundBrush", EnvironmentColors.ToolWindowTextColorKey, 0xCC);
        SetDerivedBrush(view, "ChatBorderBrush", EnvironmentColors.ToolWindowTextColorKey, 0x48);
        SetBrush(view, "ChatInputBackgroundBrush", EnvironmentColors.ComboBoxBackgroundColorKey);
        SetBrush(view, "ChatPopupBackgroundBrush", EnvironmentColors.CommandBarMenuBackgroundGradientBeginColorKey);
        SetBrush(view, "ChatHoverBrush", ThemedDialogColors.ListItemMouseOverColorKey);
        SetBrush(view, "ChatHoverForegroundBrush", ThemedDialogColors.ListItemMouseOverTextColorKey);
        SetBrush(view, "ChatSelectionBrush", ThemedDialogColors.SelectedItemActiveColorKey);
        SetBrush(view, "ChatSelectionForegroundBrush", ThemedDialogColors.SelectedItemActiveTextColorKey);
        // Claude brand accent/focus colors are owned by Core, not the current VS accent.
        SetBrush(view, "ChatUserBubbleBackgroundBrush", ThemedDialogColors.SelectedItemInactiveColorKey);
        // Diff colors are semantic (green = added, red = removed), not theme accents: translucent tints
        // read correctly over both dark and light backgrounds and keep syntax colors visible on top.
        SetFixedBrush(view, "ChatDiffAddedBackgroundBrush", 0x38, 0x2E, 0xA0, 0x43);
        SetFixedBrush(view, "ChatDiffAddedForegroundBrush", 0xFF, 0x3F, 0xB9, 0x50);
        SetFixedBrush(view, "ChatDiffRemovedBackgroundBrush", 0x38, 0xF8, 0x51, 0x49);
        SetFixedBrush(view, "ChatDiffRemovedForegroundBrush", 0xFF, 0xF8, 0x51, 0x49);
        ApplyEditorSyntaxColors(view);
        SetBrush(view, "ChatErrorForegroundBrush", EnvironmentColors.ToolWindowValidationErrorTextColorKey);
        SetBrush(view, "ChatWarningBackgroundBrush", ThemedDialogColors.PromotionBoxBackgroundColorKey);
    }

    // Token colors for the transcript's code blocks/diffs come from the editor's own classification
    // format map, so highlighted code matches whatever color theme the user's editor uses.
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

    /// <summary>The editor's "text" classification format map - the source of the syntax colors
    /// <see cref="Apply"/> reads - so the pane can re-apply them when it changes: Fonts and Colors
    /// edits raise its ClassificationFormatMappingChanged but not <see cref="VSColorTheme.ThemeChanged"/>.
    /// Null (never an exception) when the editor services are unavailable.</summary>
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
            // The transcript falls back to its built-in palette; never let theming break the window.
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
