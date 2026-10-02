using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ClaudeCode.Core.Views.Converters;

/// <summary>Decodes a base64 image payload into a small, frozen bitmap for attachment thumbnails.
/// Sources bigger than the thumbnail box are decoded with their longer side capped; smaller sources
/// are decoded as-is.</summary>
public sealed class Base64ToImageSourceConverter : IValueConverter
{
    /// <summary>
    /// The thumbnail decode width.
    /// </summary>
    private const int _thumbnailDecodeWidth = 160;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string base64 || base64.Length == 0)
        {
            return null;
        }

        try
        {
            var bytes = System.Convert.FromBase64String(base64);
            using var stream = new MemoryStream(bytes);
            var probe = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (probe.Frames.Count == 0)
            {
                return null;
            }

            BitmapFrame probeFrame = probe.Frames[0];
            int sourceWidth = probeFrame.PixelWidth, sourceHeight = probeFrame.PixelHeight;

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (sourceWidth >= sourceHeight)
            {
                if (sourceWidth > _thumbnailDecodeWidth)
                {
                    image.DecodePixelWidth = _thumbnailDecodeWidth;
                }
            }
            else if (sourceHeight > _thumbnailDecodeWidth)
            {
                image.DecodePixelHeight = _thumbnailDecodeWidth;
            }

            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
