using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Maps a session mode id/name to a Segoe MDL2 Assets glyph, mirroring the VS Code
/// extension's mode icons (hand = manual, pencil = accept edits, list = plan, bolt = auto).</summary>
public sealed class SessionModeGlyphConverter : IValueConverter
{
    /// <summary>
    /// Maps a permission mode string to a corresponding icon glyph for display in the UI, returning a plan icon for plan mode, a pencil icon for accept or edit modes, a lightning bolt icon for automatic or bypass modes, and a hand icon for manual mode by default.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>The object result.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string ?? string.Empty).ToLowerInvariant();
        if (key.Contains("plan")) return "";                       // list
        if (key.Contains("accept") || key.Contains("edit")) return ""; // pencil
        if (key.Contains("auto") || key.Contains("bypass") || key.Contains("yolo")) return ""; // lightning bolt
        return "";                                                  // touch/hand: manual
    }

    /// <summary>
    /// Converts a value back to its source type; this method is not supported and always throws a NotSupportedException.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="culture">The culture.</param>
    /// <returns>The object result.</returns>
    /// <exception cref="NotSupportedException">Thrown when an error occurs during execution.</exception>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
