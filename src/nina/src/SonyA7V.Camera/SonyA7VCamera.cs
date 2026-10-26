using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonyCamera.Client;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace SonyA7V.Camera;

/// <summary>Capability-driven Sony CrSDK NINA adapter. Class/assembly identity is retained
/// so existing A7 V profiles and installed plugin references continue to resolve.</summary>
public sealed class SonyA7VCamera : ICamera
{
    private readonly SonyCameraService service;
    private readonly IImageDataFactory factory;
    private readonly AsyncObservableCollection<BinningMode> binningModes;
    private readonly List<string> readoutModes = new() { "RAW" };
    private CancellationTokenSource? exposureCts;
    private Task? exposureTask;
    private CaptureSequence? sequence;
    private int gain = 800;
    private DateTime exposureStart = DateTime.UtcNow;
    private double exposureSeconds;

    private readonly NINA.Profile.Interfaces.IProfileService? profiles;
    private int lastLiveGain;
    private double lastLiveShutter = double.NaN;

    public SonyA7VCamera(SonyCameraService service, IImageDataFactory factory, NINA.Profile.Interfaces.IProfileService? profiles = null)
    {
        this.service = service;
        this.factory = factory;
        this.profiles = profiles;
        binningModes = new AsyncObservableCollection<BinningMode> { new BinningMode(1, 1) };
        service.Changed += () => { foreach (var name in new[] { nameof(Connected), nameof(CameraState), nameof(Gain), nameof(CameraXSize), nameof(CameraYSize), nameof(PixelSizeX), nameof(PixelSizeY), nameof(SensorType), nameof(SensorName), nameof(Name), nameof(DisplayName), nameof(Description), nameof(Gains), nameof(GainMin), nameof(GainMax), nameof(ExposureMin), nameof(ExposureMax), nameof(CanShowLiveView), nameof(HasBattery), nameof(BatteryLevel) }) Raise(name); };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A subscriber throwing must never abort an exposure: NINA reports that as a download failure.</summary>
    private void Raise([CallerMemberName] string? name = null)
    {
        try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        catch (Exception ex) { Logger.Error("[SonyA7V] PropertyChanged subscriber failed for " + name + ": " + ex.Message, ex); }
    }

    public string Id => "SonyA7V-ILCE-7M5-SonyCrSDK";
    public string Name => service.Model;
    public string DisplayName => service.Facts is null ? "Sony CrSDK Camera" : $"Sony {service.Model} (CrSDK)";
    public string Category => "Sony";
    public string Description => $"{service.Model} through Sony Camera Remote SDK; capability-driven static RAW capture. Hardware validation is model-specific.";
    public string DriverInfo => $"Sony Camera Remote SDK 2.02.00 / project wrapper feature ABI 1 / camera driver {DriverVersion}";
    public string DriverVersion => typeof(SonyA7VCamera).Assembly.GetName().Version?.ToString() ?? "unknown";
    public bool HasSetupDialog => true;
    public void SetupDialog() => AstroSettingsWindow.Show(service);
    public IList<string> SupportedActions => new[]{"Sony.GetAstroStatus","Sony.PrepareAstroSession","Sony.RestoreAstroSession","Sony.SetAperture"};
    public string Action(string actionName, string actionParameters) {
        switch(actionName) {
        case "Sony.GetAstroStatus":
            return JsonSerializer.Serialize(new{model=service.Model,connected=Connected,unconfirmed=service.Unconfirmed,
                geometry=service.Geometry,pixelSizeMicrons=double.IsFinite(PixelSizeX)?PixelSizeX:(double?)null,
                batteryPercent=service.Battery?.Percent,realTimeSensorTemperatureC=(double?)null,
                ordinaryShutters=service.Facts?.Shutters,exposureCapabilities=service.Facts?.Capabilities,
                preparation=service.PreparationReceipt?.Changes});
        case "Sony.PrepareAstroSession":
            var options=JsonSerializer.Deserialize<AstroPreparationOptions>(actionParameters,new JsonSerializerOptions{
                PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})
                ??throw new ArgumentException(Text.T("Provide an explicit JSON astronomy preparation plan"));
            return JsonSerializer.Serialize(Task.Run(()=>service.PrepareAstroAsync(options)).GetAwaiter().GetResult().Changes);
        case "Sony.RestoreAstroSession":
            if(!string.IsNullOrWhiteSpace(actionParameters))throw new ArgumentException(Text.T("Restore takes no parameters; it uses the same-session receipt"));
            Task.Run(()=>service.RestoreAstroAsync()).GetAwaiter().GetResult();return "restored";
        case "Sony.SetAperture":
            var fNumber=double.Parse(actionParameters,NumberStyles.Float,CultureInfo.InvariantCulture);
            return Task.Run(()=>service.SetApertureAsync(fNumber)).GetAwaiter().GetResult().ToString("R",CultureInfo.InvariantCulture);
        default:throw new NotSupportedException(Text.T("Unknown Sony action: ")+actionName);
        }
    }
    public void SendCommandBlind(string command, bool raw = true) => throw new NotSupportedException(Text.T("No raw command pass-through"));
    public bool SendCommandBool(string command, bool raw = true) => throw new NotSupportedException(Text.T("No raw command pass-through"));
    public string SendCommandString(string command, bool raw = true) => throw new NotSupportedException(Text.T("No raw command pass-through"));

    public bool Connected => service.Connected;

    public async Task<bool> Connect(CancellationToken token)
    {
        await service.ConnectAsync(factory, token).ConfigureAwait(false);
        var facts = service.Facts ?? throw new InvalidOperationException(Text.T("Camera facts were not read"));
        gain = checked((int)facts.Parameters.Iso);
        Raise(nameof(Connected));
        Raise(nameof(CameraState));
        Raise(nameof(Gain));
        return true;
    }

    public void Disconnect()
    {
        try { Task.Run(() => service.DisconnectAsync()).GetAwaiter().GetResult(); }
        catch (Exception ex) { Logger.Error("[SonyA7V] Disconnect reported a camera that is not idle", ex); }
        Raise(nameof(Connected));
        Raise(nameof(CameraState));
    }

    public CameraStates CameraState => service.Unconfirmed ? CameraStates.Error
        : exposureTask is { IsCompleted: false } ? CameraStates.Exposing
        : service.Connected ? CameraStates.Idle
        : CameraStates.NoState;

    public short BinX { get; set; } = 1;
    public short BinY { get; set; } = 1;
    public short MaxBinX => 1;
    public short MaxBinY => 1;
    public AsyncObservableCollection<BinningMode> BinningModes => binningModes;
    public void SetBinning(short x, short y)
    {
        if (x != 1 || y != 1) throw new ArgumentOutOfRangeException(nameof(x), "Only 1x1 binning is supported; no software resampling is reported as hardware binning");
        BinX = 1; BinY = 1;
    }

    public int CameraXSize => service.Geometry?.Width ?? 0;
    public int CameraYSize => service.Geometry?.Height ?? 0;
    public double PixelSizeX => service.PixelSizeMicrons;
    public double PixelSizeY => service.PixelSizeMicrons;
    public short BayerOffsetX => 0; // CFA is reported for the decoded active origin.
    public short BayerOffsetY => 0;
    public int BitDepth => 16; // unpacked ushort container, not a claim of sensor ADC depth
    public string SensorName => service.Model;
    public SensorType SensorType => service.Geometry is { } g ? Enum.Parse<SensorType>(g.Cfa) : SensorType.RGGB;
    public double ElectronsPerADU => 1; // not characterised; 1 is NINA's "unknown" convention
    public double Temperature => double.NaN; // no real-time sensor temperature in this SDK path
    public bool CanSetTemperature => false;
    public double TemperatureSetPoint { get => double.NaN; set => throw new NotSupportedException(Text.T("No cooler on this camera")); }
    public bool CoolerOn { get => false; set => throw new NotSupportedException(Text.T("No cooler on this camera")); }
    public double CoolerPower => 0;
    public bool HasDewHeater => false;
    public bool DewHeaterOn { get => false; set => throw new NotSupportedException(Text.T("No dew heater control")); }
    public bool HasBattery => Connected && service.Battery?.Percent is not null;
    public int BatteryLevel => HasBattery ? service.Battery!.Percent!.Value : -1; // unknown is never fabricated as zero

    public bool CanSubSample => false;
    public bool EnableSubSample { get; set; }
    public int SubSampleX { get; set; }
    public int SubSampleY { get; set; }
    public int SubSampleWidth { get; set; }
    public int SubSampleHeight { get; set; }
    public void UpdateSubSampleArea() => throw new NotSupportedException(Text.T("Sensor ROI is not exposed by this SDK path"));
    public bool HasShutter => true;

    public bool CanShowLiveView => service.Facts?.LiveViewAvailable == true;
    public bool LiveViewEnabled => service.LiveViewActive;

    public void StartLiveView(CaptureSequence sequence)
    {
        // Live view never goes through StartExposure, so the sequence's gain/shutter must be pushed
        // to the camera here; otherwise NINA's parameter changes would never reach the body.
        try
        {
            Task.Run(async () => {
                uint? iso = sequence is {Gain: > 0} ? (uint)sequence.Gain : null;
                double? shutter = sequence is {ExposureTime: > 0} && IsShutterCandidate(sequence.ExposureTime) ? sequence.ExposureTime : null;
                await service.ConfigureLiveViewAsync(iso, shutter).ConfigureAwait(false);
                if (iso.HasValue) lastLiveGain = (int)iso.Value;
                if (shutter.HasValue) lastLiveShutter = shutter.Value;
            }).GetAwaiter().GetResult();
        }
        catch (AggregateException ex) { throw ex.GetBaseException(); }
        Raise(nameof(LiveViewEnabled));
    }

    private bool IsShutterCandidate(double seconds)
    {
        var shutters = service.Facts?.Shutters;
        return shutters is not null && shutters.Contains(seconds);
    }

    public async Task<IExposureData> DownloadLiveView(CancellationToken token)
    {
        // NINA keeps the snapshot panel's Gain/ExposureDuration in the profile and does not push
        // them to the driver during live view, so mirror the user's changes here.
        if (profiles is not null)
        {
            var snap = profiles.ActiveProfile.SnapShotControlSettings;
            var newGain = snap.Gain > 0 && snap.Gain != lastLiveGain ? snap.Gain : 0;
            var newShutter = snap.ExposureDuration > 0 && IsShutterCandidate(snap.ExposureDuration)
                && !(Math.Abs(snap.ExposureDuration - lastLiveShutter) < 1e-9) ? snap.ExposureDuration : double.NaN;
            if (newGain != 0 || !double.IsNaN(newShutter))
            {
                // Writing device properties while the preview stream is running makes the Sony SDK
                // answer OperationInvalid and latches the session; stop, write, then restart.
                try
                {
                    await service.ConfigureLiveViewAsync(newGain == 0 ? null : (uint)newGain,
                        double.IsNaN(newShutter) ? null : newShutter, token).ConfigureAwait(false);
                    if (newGain != 0) lastLiveGain = newGain;
                    if (!double.IsNaN(newShutter)) lastLiveShutter = newShutter;
                }
                catch (Exception ex)
                {
                    Logger.Error("[SonyA7V] Live view parameter change needs a clean reconnect: " + ex.Message, ex);
                    throw;
                }
            }
        }
        var frame = await service.GetLiveViewFrameAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException(Text.T("The camera has no live view frame ready yet"));
        var color = service.Settings.LiveViewColor;
        var pixels = JpegPreview.ToPixels(frame.Jpeg, color, out var width, out var height);
        var meta = new ImageMetaData();
        meta.Camera.SensorType = color ? SensorType.RGGB : SensorType.Monochrome;
        if (color) meta.Camera.BayerPattern = BayerPatternEnum.RGGB;
        meta.Camera.Name = DisplayName;
        meta.Image.ImageType = "PREVIEW";
        // Only colour previews are Bayered; monochrome previews bypass debayering.
        var data = factory.CreateBaseImageData(pixels, width, height, 16, color, meta);
        return new LiveViewExposure(data);
    }

    public void StopLiveView()
    {
        // Never block NINA's UI thread here: the native stop queues behind any in-flight frame read.
        _ = Task.Run(async () =>
        {
            try { await service.StopLiveViewAsync().ConfigureAwait(false); Logger.Info("[SonyA7V] Live view stopped"); }
            catch (Exception ex) { Logger.Error("[SonyA7V] StopLiveView failed: " + ex.Message, ex); }
        });
        Raise(nameof(LiveViewEnabled));
    }

    /// <summary>
    /// One owned preview frame: decoded JPEG luma from the camera's own live view stream.
    /// It is not sensor RAW, not colour-managed and not usable for photometry.
    /// </summary>
    private sealed class LiveViewExposure(IImageData data) : IExposureData
    {
        public int BitDepth => data.Properties.BitDepth;
        public ImageMetaData MetaData => data.MetaData;
        public Task<IImageData> ToImageData(IProgress<ApplicationStatus>? progress = null, CancellationToken cancelToken = default)
        {
            cancelToken.ThrowIfCancellationRequested();
            return Task.FromResult(data);
        }
    }

    public IList<string> ReadoutModes => readoutModes;
    public short ReadoutMode { get => 0; set { if (value != 0) throw new ArgumentOutOfRangeException(nameof(value), "Only one readout mode"); } }
    public short ReadoutModeForNormalImages { get => 0; set { if (value != 0) throw new ArgumentOutOfRangeException(nameof(value), "Only one readout mode"); } }
    public short ReadoutModeForSnapImages { get => 0; set { if (value != 0) throw new ArgumentOutOfRangeException(nameof(value), "Only one readout mode"); } }

    public bool CanGetGain => true;
    public bool CanSetGain => true;
    public int Gain
    {
        get => gain;
        set
        {
            var facts = service.Facts;
            if (facts is null) { gain = value; return; }
            if (!facts.Isos.Contains((uint)value))
                throw new ArgumentOutOfRangeException(nameof(value), Text.F($"ISO {value} is not offered by this camera"));
            gain = value;
            Raise();
            // Deliberately no write here: live view applies panel changes by restarting the preview.
        }
    }
    public int GainMin => service.Facts is { } f ? f.Isos.Min(x => (int)x) : 0;
    public int GainMax => service.Facts is { } f ? f.Isos.Max(x => (int)x) : 0;
    public IList<int> Gains => service.Facts is { } f ? f.Isos.Select(x => (int)x).ToList() : new List<int>();

    public bool CanSetOffset => false;
    public int Offset { get => 0; set => throw new NotSupportedException(Text.T("No hardware offset control")); }
    public int OffsetMin => 0;
    public int OffsetMax => 0;
    public bool CanSetUSBLimit => false;
    public int USBLimit { get => 0; set => throw new NotSupportedException(Text.T("No host USB bandwidth control")); }
    public int USBLimitMin => 0;
    public int USBLimitMax => 0;
    public int USBLimitStep => 0;

    public double ExposureMin => service.Facts is { } f && f.Shutters.Count > 0 ? f.Shutters.Min() : 0.000125;
    public double ExposureMax => service.Facts is { } f ? SonyCamera.Client.ExposurePolicy.Maximum(f.Shutters, f.Capabilities) : 0;

    public void StartExposure(CaptureSequence captureSequence)
    {
        ArgumentNullException.ThrowIfNull(captureSequence);
        if (!Connected) throw new InvalidOperationException(Text.T("Camera is not connected"));
        if (exposureTask is { IsCompleted: false }) throw new InvalidOperationException(Text.T("An exposure is already running"));
        if (captureSequence.Binning.X != 1 || captureSequence.Binning.Y != 1)
            throw new NotSupportedException(Text.T("Only 1x1 binning is supported"));
        sequence = captureSequence;
        gain = captureSequence.Gain > 0 ? captureSequence.Gain : checked((int)(service.Facts?.Parameters.Iso ?? 0));
        exposureSeconds = captureSequence.ExposureTime;
        exposureStart = DateTime.UtcNow;
        exposureCts?.Dispose();
        exposureCts = new CancellationTokenSource();
        var token = exposureCts.Token;
        var prefix = "NINA";
        Logger.Info($"[SonyA7V] StartExposure request seconds={captureSequence.ExposureTime:R} gain={captureSequence.Gain} bin={captureSequence.Binning?.X}x{captureSequence.Binning?.Y} type={captureSequence.ImageType}");
        service.PrepareExposureGate();
        exposureTask = Task.Run(async () =>
        {
            try
            {
                await service.StartExposureAsync(captureSequence.ExposureTime, captureSequence.Gain, prefix, token).ConfigureAwait(false);
                await service.WaitAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("[SonyA7V] Exposure task failed: " + ex.Message, ex);
                service.FailExposureGate(ex);
                throw;
            }
        }, CancellationToken.None);
        Raise(nameof(CameraState));
    }

    public async Task WaitUntilExposureIsReady(CancellationToken token)
    {
        var started = DateTime.UtcNow;
        try
        {
            // Only the exposure phase: NINA budgets this as exposure time plus a small allowance.
            await service.WaitExposureReadyAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"[SonyA7V] Wait failed after {(DateTime.UtcNow - started).TotalSeconds:F2}s: {ex.GetType().Name}: {ex.Message}; hostTokenCancelled={token.IsCancellationRequested}", ex);
            throw;
        }
        Logger.Info($"[SonyA7V] Exposure ready after {(DateTime.UtcNow - started).TotalSeconds:F2}s (download deferred); connected={Connected}");
        Raise(nameof(CameraState));
    }

    public async Task<IExposureData> DownloadExposure(CancellationToken token)
    {
        Logger.Info("[SonyA7V] DownloadExposure entered");
        ArwImageFrame frame;
        try
        {
            var started = DateTime.UtcNow;
            await service.WaitAsync(token).ConfigureAwait(false);
            frame = await service.LoadAsync(token).ConfigureAwait(false);
            Logger.Info($"[SonyA7V] DownloadExposure completed in {(DateTime.UtcNow - started).TotalSeconds:F2}s (rawAttached={service.Settings.AttachOriginalRaw})");
            if (service.Settings.CopyOriginalRaw) CopyOriginalRaw(service.Settings);
        }
        catch (Exception ex) { Logger.Error("[SonyA7V] DownloadExposure failed: " + ex.Message, ex); throw; }
        var meta = frame.MetaData;
        meta.Image.ExposureTime = exposureSeconds;
        meta.Image.ImageType = sequence?.ImageType ?? "LIGHT";
        meta.Image.Binning = "1x1";
        meta.Image.SetExposureTimes(exposureStart, exposureStart + TimeSpan.FromSeconds(exposureSeconds));
        var actual=service.LastCapture?.ActualParameters ?? throw new InvalidDataException(Text.T("Capture has no verified camera parameters"));
        meta.Camera.Gain = checked((int)actual.Iso);
        meta.Camera.BinX = 1; meta.Camera.BinY = 1;
        meta.Camera.ReadoutModeIndex = 0;
        meta.Camera.ReadoutModeName = service.CaptureFrame.ToString();
        meta.Camera.PixelSize = PixelSizeX;
        meta.Camera.Temperature = double.NaN;
        meta.Camera.ElectronsPerADU = ElectronsPerADU;
        meta.Camera.Id = Id;
        meta.Camera.Name = DisplayName;
        Raise(nameof(CameraState));
        return frame;
    }

    /// <summary>Copies the camera's original ARW next to NINA's own output when the user asked to keep it.</summary>
    private void CopyOriginalRaw(PluginSettings settings)
    {
        try
        {
            var source = service.LastCapture?.ArwPath;
            var folder = profiles?.ActiveProfile.ImageFileSettings.FilePath;
            if (string.IsNullOrEmpty(source) || !File.Exists(source) || string.IsNullOrWhiteSpace(folder)) return;
            var targetDirectory = Path.Combine(folder, settings.ValidatedRawCopyFolder);
            Directory.CreateDirectory(targetDirectory);
            var name = Path.GetFileName(source);
            var target = Path.Combine(targetDirectory, name);
            for (var n = 1; File.Exists(target); n++) target = Path.Combine(targetDirectory, Path.GetFileNameWithoutExtension(name) + "(" + n + ")" + Path.GetExtension(name));
            File.Copy(source, target);
            Logger.Info("[SonyA7V] Original RAW kept at " + target);
            if(service.LastCapture is {} delivered)service.CompleteDelivery(delivered);
        }
        catch (Exception ex) { Logger.Error("[SonyA7V] Could not keep the original RAW: " + ex.Message, ex); }
    }
    public void AbortExposure()
    {
        try { exposureCts?.Cancel(); } catch (ObjectDisposedException) { }
        // NINA may disconnect or reconnect immediately after this call, so the native cancel and the
        // idle proof must finish here instead of racing in the background.
        try { Task.Run(() => service.AbortAsync()).Wait(TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { Logger.Error("[SonyA7V] Abort failed", ex); }
        Raise(nameof(CameraState));
    }

    public void StopExposure()
    {
        _ = Task.Run(async () => { try { await service.EndEarlyAsync().ConfigureAwait(false); } catch (Exception ex) { Logger.Error("[SonyA7V] Stop failed", ex); } });
        Raise(nameof(CameraState));
    }
}
