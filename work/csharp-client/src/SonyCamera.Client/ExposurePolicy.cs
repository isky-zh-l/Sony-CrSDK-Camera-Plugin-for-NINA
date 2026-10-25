namespace SonyCamera.Client;

/// <summary>Choose an exact camera shutter first, then explicitly supported software Bulb.
/// No model, 30-second threshold, rounding, or fabricated hardware capability.</summary>
public static class ExposurePolicy {
    public static ExposureRequest Create(double seconds, IReadOnlyList<double> shutters, ExposureCapabilities caps) {
        if (!double.IsFinite(seconds) || seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Exposure must be finite and positive");
        if (shutters.Any(s => double.IsFinite(s) && s > 0 && s == seconds)) {
            var timeout = Math.Ceiling((seconds + 15) * 1000) / 1000;
            if (timeout > caps.MaximumExposureTimeoutSeconds)
                throw new ArgumentOutOfRangeException(nameof(seconds), "Camera shutter exceeds the wrapper timeout policy");
            return new(ExposureMode.Ordinary, seconds, timeout, Math.Min(180, caps.MaximumDownloadTimeoutSeconds));
        }
        if (!caps.BulbAvailable)
            throw new NotSupportedException($"Exposure {seconds:R}s is not a camera shutter candidate and this camera does not offer Bulb in its current mode");
        if (seconds < caps.BulbMinimumSeconds || seconds > caps.BulbMaximumSeconds ||
            Math.Round(seconds * 1000) / 1000 != seconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), $"Software Bulb requires exact milliseconds within {caps.BulbMinimumSeconds}..{caps.BulbMaximumSeconds}s");
        var bulbTimeout = Math.Ceiling((seconds + 30) * 1000) / 1000;
        if (bulbTimeout > caps.MaximumExposureTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Bulb exceeds the wrapper timeout policy");
        return new(ExposureMode.Bulb, seconds, bulbTimeout, Math.Min(240, caps.MaximumDownloadTimeoutSeconds));
    }
    public static double Maximum(IReadOnlyList<double> shutters, ExposureCapabilities caps) =>
        Math.Max(shutters.Where(s => double.IsFinite(s) && s > 0 && s + 15 <= caps.MaximumExposureTimeoutSeconds).DefaultIfEmpty(0).Max(),
            caps.BulbAvailable ? Math.Min(caps.BulbMaximumSeconds, caps.MaximumExposureTimeoutSeconds - 30) : 0);
}
