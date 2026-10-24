using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonyA7V.Camera;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var directory = Path.Combine(Path.GetTempPath(), "sony-settings-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var defaults = PluginSettings.Load(directory);
            Check(defaults.LiveViewColor && !defaults.CopyOriginalRaw && !defaults.AttachOriginalRaw, "Safe defaults: no extra RAW copy");
            File.WriteAllText(Path.Combine(directory, "settings.json"), """
                { "saveOriginalRaw": "never", "rawCopyFolder": "..", "liveViewColor": false }
                """);
            var settings = PluginSettings.Load(directory);
            Check(!settings.LiveViewColor && !settings.CopyOriginalRaw && !settings.AttachOriginalRaw, "JSON user switches");
            Check(settings.ValidatedRawCopyFolder == "raw", "Parent directory is not a raw subfolder");
            Check(new PluginSettings { SaveOriginalRaw = "also" }.CopyOriginalRaw, "Keep original copy");
            Check(new PluginSettings { SaveOriginalRaw = "only" }.AttachOriginalRaw, "Legacy RAW-only switch");

            // A solid red JPEG makes accidental Bayer conversion in grayscale easily detectable.
            var rgb = new byte[4 * 4 * 3];
            for (var i = 0; i < rgb.Length; i += 3) rgb[i + 2] = 255;
            var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr24, null, rgb, 12);
            var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            var jpeg = stream.ToArray();
            var color = JpegPreview.ToPixels(jpeg, true, out var cw, out var ch);
            var gray = JpegPreview.ToPixels(jpeg, false, out var gw, out var gh);
            Check(cw == 4 && ch == 4 && gw == 4 && gh == 4, "Preview geometry");
            Check(color[0] > 64000 && color[1] < 600 && color[5] < 600, "Color RGGB channel selection");
            Check(gray.All(p => p == gray[0]) && gray[0] > 18000 && gray[0] < 21000, "Monochrome uniform luma");
            Console.WriteLine("PASS: default/JSON settings, RAW modes, subfolder, JPEG color and monochrome preview.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
