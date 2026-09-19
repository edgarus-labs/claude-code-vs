using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Decodes a base64 image payload into a small, frozen bitmap for attachment thumbnails.
/// Wide sources are decoded at a capped width so a large pasted screenshot never costs full-size
/// memory in the composer; narrow sources are decoded as-is rather than scaled up.</summary>
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
            // Read the frame header first. DecodePixelWidth rescales in *both* directions, so setting
            // it unconditionally would upscale a narrow source instead of bounding it: a 2x10,000,000
            // PNG (a few KB on the wire) would ask WIC for a 160x800,000,000 frame on the UI thread.
            var probe = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (probe.Frames.Count == 0) return null;
            int sourceWidth = probe.Frames[0].PixelWidth;

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (sourceWidth > ThumbnailDecodeWidth) image.DecodePixelWidth = ThumbnailDecodeWidth;
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
