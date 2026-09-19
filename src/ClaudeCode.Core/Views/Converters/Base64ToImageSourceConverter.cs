using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Decodes a base64 image payload into a small, frozen bitmap for attachment thumbnails.
/// Decode width is capped so a large pasted screenshot never costs full-size memory in the composer.</summary>
public sealed class Base64ToImageSourceConverter : IValueConverter
{
    private const int ThumbnailDecodeWidth = 160;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string base64 || base64.Length == 0) return null;
        try
        {
            var bytes = System.Convert.FromBase64String(base64);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = ThumbnailDecodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // Undecodable payload: the chip falls back to the generic image icon.
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
