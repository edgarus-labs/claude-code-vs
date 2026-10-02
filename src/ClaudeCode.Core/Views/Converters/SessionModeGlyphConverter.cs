using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Maps a session mode id/name to a Segoe MDL2 Assets glyph
/// (hand = manual, pencil = accept edits, list = plan, bolt = auto).</summary>
public sealed class SessionModeGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string ?? string.Empty).ToLowerInvariant();
        if (key.Contains("plan"))
        {
            return "";
        }

        if (key.Contains("accept") || key.Contains("edit"))
        {
            return "";
        }

        if (key.Contains("auto") || key.Contains("bypass") || key.Contains("yolo"))
        {
            return "";
        }

        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
