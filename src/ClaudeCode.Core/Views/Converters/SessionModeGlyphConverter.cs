using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Maps a session mode id/name to a Segoe MDL2 Assets glyph, mirroring the VS Code
/// extension's mode icons (hand = manual, pencil = accept edits, list = plan, bolt = auto).</summary>
public sealed class SessionModeGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string ?? string.Empty).ToLowerInvariant();
        if (key.Contains("plan")) return "";                       // list
        if (key.Contains("accept") || key.Contains("edit")) return ""; // pencil
        if (key.Contains("auto") || key.Contains("bypass") || key.Contains("yolo")) return ""; // lightning bolt
        return "";                                                  // touch/hand: manual
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
