using System.IO;
using SonyCamera.Client;

namespace SonyA7V.Camera;

/// <summary>SDK 2.02 inventory, not a hardware compatibility whitelist. Runtime properties decide readiness.</summary>
public static class CameraSupport {
    public const string SdkVersion = "2.02.00";
    public static IReadOnlyList<string> SdkModels { get; } = Array.AsReadOnly(new[] {
        "ILCE-7RM4", "ILCE-9M2", "ILCE-7C", "ILCE-7SM3", "ILCE-1", "ILCE-7RM4A",
        "DSC-RX0M2", "ILCE-7M4", "ILME-FX3", "ILME-FX30", "ILME-FX6", "ILCE-7RM5",
        "ZV-E1", "ILCE-6700", "ILCE-7CM2", "ILCE-7CR", "ILX-LR1", "MPC-2610",
        "ILCE-9M3", "ZV-E10M2", "PXW-Z200", "HXR-NX800", "ILCE-1M2", "ILME-FX3A",
        "BRC-AM7", "ILME-FR7", "ILME-FX2", "DSC-RX1RM3", "ILCE-7M5", "PXW-Z300", "PXW-Z380", "ILCE-7RM6"
    });
    public static string Normalize(string model) => model.Trim().ToUpperInvariant();
    public static CameraDescription Select(IReadOnlyList<CameraDescription> cameras, string? requestedModel) {
        var choices = string.IsNullOrWhiteSpace(requestedModel) ? cameras.ToArray() :
            cameras.Where(c => Normalize(c.Model) == Normalize(requestedModel)).ToArray();
        if (choices.Length != 1)
            throw new InvalidOperationException($"Expected exactly one Sony camera{(string.IsNullOrWhiteSpace(requestedModel) ? "" : " matching cameraModel=" + requestedModel)}; found {choices.Length}. Detected: " +
                string.Join(", ", cameras.Select(c => c.Model + " (" + c.Connection + ")")) + ". Connect one body or set an unambiguous cameraModel in settings.json.");
        return choices[0];
    }
}

/// <summary>Only A7M5 has a measured bootstrap. Others remain unknown until a RAW is decoded
/// or the user supplies geometry. Every downloaded RAW replaces bootstrap geometry.</summary>
public sealed record CameraGeometry(int Width, int Height, string Cfa) {
    public static CameraGeometry? Bootstrap(string model, PluginSettings settings) {
        if (settings.SensorWidth is { } w && settings.SensorHeight is { } h)
            return new(w, h, settings.BayerPattern);
        return CameraSupport.Normalize(model) == "ILCE-7M5" ? new(7028, 4688, "RGGB") : null;
    }
    public static CameraGeometry FromRaw(RawInfo info) {
        if (info.Width == 0 || info.Height == 0 || (ulong)info.Width * info.Height > 200_000_000 ||
            info.Width > 20000 || info.Height > 20000 ||
            (ulong)info.Left + info.Width > info.RawWidth || (ulong)info.Top + info.Height > info.RawHeight ||
            info.Pitch < (ulong)info.RawWidth * 2 || info.Cfa is not ("RGGB" or "BGGR" or "GRBG" or "GBRG"))
            throw new InvalidDataException("Invalid RAW active geometry or unsupported Bayer pattern");
        return new(checked((int)info.Width), checked((int)info.Height), info.Cfa);
    }
}
