using System.IO;
using NINA.Core.Utility;
using System.Text.Json;

namespace SonyA7V.Camera;

/// <summary>
/// User-facing plugin settings, read from settings.json next to the plugin assembly.
/// Separate from sony-a7v.json, which pins deployment identity and is not meant for end users.
/// </summary>
public sealed class PluginSettings
{
    /// <summary>never = FITS only; also = FITS plus a copy of the camera's original ARW; only = ARW only.</summary>
    public string SaveOriginalRaw { get; init; } = "never";

    /// <summary>Subfolder used for the original RAW copies when SaveOriginalRaw = also.</summary>
    public string RawCopyFolder { get; init; } = "raw";

    /// <summary>False produces monochrome JPEG previews; never changes captured RAW pixels.</summary>
    public bool LiveViewColor { get; init; } = true;

    public bool AttachOriginalRaw => string.Equals(SaveOriginalRaw, "only", StringComparison.OrdinalIgnoreCase);
    public bool CopyOriginalRaw => string.Equals(SaveOriginalRaw, "also", StringComparison.OrdinalIgnoreCase);

    public string ValidatedRawCopyFolder =>
        string.IsNullOrWhiteSpace(RawCopyFolder) || RawCopyFolder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? "raw"
            : RawCopyFolder.Trim() is "." or ".." ? "raw" : RawCopyFolder.Trim();

    public static PluginSettings Load(string pluginDirectory)
    {
        var path = Path.Combine(pluginDirectory, "settings.json");
        if (!File.Exists(path)) return new PluginSettings();
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            return JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(path), options) ?? new PluginSettings();
        }
        catch (Exception ex)
        {
            // A broken settings file must not stop imaging; fall back to the safe default.
            Logger.Error("[SonyA7V] settings.json could not be read; using defaults: " + ex.Message, ex);
            return new PluginSettings();
        }
    }
}
