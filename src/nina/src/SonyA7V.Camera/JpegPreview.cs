using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SonyA7V.Camera;

/// <summary>
/// Turns one camera live view JPEG into an RGGB Bayer mosaic so NINA debayers it back to colour.
/// The camera streams JPEG, not RAW: this is a framing/focus preview, never a photometric source.
/// </summary>
internal static class JpegPreview
{
    internal static ushort[] ToPixels(byte[] jpeg, bool color, out int width, out int height)
    {
        if (jpeg.Length == 0) throw new InvalidDataException(Text.T("Empty live view frame"));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = new MemoryStream(jpeg, writable: false);
        image.EndInit();
        image.Freeze();

        // Keep dimensions even so every 2x2 Bayer quad is complete.
        width = image.PixelWidth & ~1;
        height = image.PixelHeight & ~1;
        if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024)
            throw new InvalidDataException(Text.F($"Implausible live view geometry {image.PixelWidth}x{image.PixelHeight}"));

        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        var stride = image.PixelWidth * 3;
        var bytes = new byte[stride * image.PixelHeight];
        converted.CopyPixels(bytes, stride, 0);

        // RGGB quad: (0,0)=R (0,1)=G (1,0)=G (1,1)=B. Values are 8-bit scaled into the 16-bit container.
        var pixels = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            var outRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 3;
                var b = bytes[i];
                var g = bytes[i + 1];
                var r = bytes[i + 2];
                var even = (y & 1) == 0;
                var value = (x & 1) == 0
                    ? (even ? r : g)
                    : (even ? g : b);
                // Integer Rec.601 luma for a display preview, scaled to 16 bits.
                pixels[outRow + x] = color
                    ? (ushort)(value * 257)
                    : (ushort)(((77 * r + 150 * g + 29 * b + 128) >> 8) * 257);
            }
        }
        return pixels;
    }
}
