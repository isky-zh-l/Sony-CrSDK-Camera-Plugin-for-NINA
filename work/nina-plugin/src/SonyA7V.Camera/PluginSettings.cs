using System.IO;
using NINA.Core.Utility;
using System.Text.Json;

using SonyCamera.Client;

namespace SonyA7V.Camera;

/// <summary>
/// User-facing plugin settings, read from settings.json next to the plugin assembly.
/// Separate from sony-a7v.json, which pins deployment identity and is not meant for end users.
/// </summary>
public sealed record PluginSettings
{
    /// <summary>never = FITS only; also = FITS plus a copy of the camera's original ARW; only = ARW only.</summary>
    public string SaveOriginalRaw { get; init; } = "never";

    /// <summary>Subfolder used for the original RAW copies when SaveOriginalRaw = also.</summary>
    public string RawCopyFolder { get; init; } = "raw";

    /// <summary>False produces monochrome JPEG previews; never changes captured RAW pixels.</summary>
    public bool LiveViewColor { get; init; } = true;

    /// <summary>Optional exact SDK model selector. Ambiguous choices are refused, never picked by index.</summary>
    public string? CameraModel { get; init; }
    /// <summary>Optional bootstrap dimensions; actual decoded RAW dimensions always take precedence.</summary>
    public int? SensorWidth { get; init; }
    public int? SensorHeight { get; init; }
    public string BayerPattern { get; init; } = "RGGB";
    /// <summary>Physical pixel pitch in micrometres; unknown remains NaN, never borrowed from A7M5.</summary>
    public double? PixelSizeMicrons { get; init; }

    public AstroPreparationOptions? AstroPreparation { get; init; }
    public AstroPreparationOptions? AstroPreparationSelection { get; init; }
    /// <summary>Experimental, shared-session lens focus provider. Requires camera connected first.</summary>
    public bool EnableLensFocuser { get; init; }

    public void ValidateCameraSettings() {
        if (SensorWidth.HasValue != SensorHeight.HasValue ||
            SensorWidth is <= 0 or > 20000 || SensorHeight is <= 0 or > 20000 ||
            (long)(SensorWidth ?? 0) * (SensorHeight ?? 0) > 200_000_000)
            throw new InvalidDataException("sensorWidth and sensorHeight must be supplied together with valid positive RAW dimensions");
        if (BayerPattern is not ("RGGB" or "BGGR" or "GRBG" or "GBRG"))
            throw new InvalidDataException("bayerPattern must be RGGB, BGGR, GRBG or GBRG");
        if (PixelSizeMicrons.HasValue && (!double.IsFinite(PixelSizeMicrons.Value) || PixelSizeMicrons <= 0 || PixelSizeMicrons > 100))
            throw new InvalidDataException("pixelSizeMicrons must be finite and within (0, 100]");
    }

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
