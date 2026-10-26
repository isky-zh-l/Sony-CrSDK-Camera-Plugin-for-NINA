using System.IO;
using System.Text.Json;
using NINA.Core.Utility;
using NINA.Image.Interfaces;
using SonyCamera.Client;

namespace SonyA7V.Camera;

/// <summary>Deployment facts read from the plugin directory. No absolute developer path is assumed.</summary>
public sealed class CameraDeployment
{
    public string ManifestPath { get; init; } = "native/approved-native.json";
    public string ApprovedManifestSha256 { get; init; } = "";
    public string ReceiveRoot { get; init; } = "";

    public static CameraDeployment Load(string pluginDirectory)
    {
        var path = Path.Combine(pluginDirectory, "sony-a7v.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Plugin deployment configuration missing: " + path +
                ". Provide manifestPath, approvedManifestSha256 and receiveRoot.", path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var deployment = JsonSerializer.Deserialize<CameraDeployment>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("Empty plugin deployment configuration");
        if (!Path.IsPathFullyQualified(deployment.ManifestPath))
            deployment = new CameraDeployment
            {
                ManifestPath = Path.GetFullPath(Path.Combine(pluginDirectory, deployment.ManifestPath)),
                ApprovedManifestSha256 = deployment.ApprovedManifestSha256,
                ReceiveRoot = deployment.ReceiveRoot
            };
        if (string.IsNullOrWhiteSpace(deployment.ReceiveRoot))
            deployment = new CameraDeployment
            {
                ManifestPath = deployment.ManifestPath,
                ApprovedManifestSha256 = deployment.ApprovedManifestSha256,
                ReceiveRoot = Path.Combine(Path.GetTempPath(), "SonyA7V")
            };
        return deployment;
    }
}

/// <summary>Camera facts frozen at connect time; never re-read behind the sequencer's back.</summary>
public sealed record CameraFacts(
    CameraParameters Parameters,
    IReadOnlyList<uint> Isos,
    IReadOnlyList<double> Shutters,
    ExposureCapabilities Capabilities,
    CameraDescription? Description = null,
    bool LiveViewAvailable = true);

/// <summary>
/// One process, one native session, one owner thread. NINA's connect/expose/download calls are
/// serialized here; the managed client already pins every native call to its own thread.
/// </summary>
public sealed partial class SonyCameraService
{
    private readonly SemaphoreSlim opGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateGate = new();
    private SonyCameraClient? client;
    private ArwImageLoader? loader;
    private CameraDeployment? deployment;
    private CaptureSnapshot? last;
    private Task<CaptureSnapshot>? active;
    private TaskCompletionSource<CaptureSnapshot>? ready;
    private bool unconfirmed;
    private string lastError = "";

    public static SonyCameraService Shared { get; } = new();

    public string LastError { get { lock (stateGate) return lastError; } }
    /// <summary>A capture keeps the session connected: NINA checks Connected during download.</summary>
    public bool Connected => Facts is not null && (client?.State is SessionState.Connected or SessionState.Capturing);
    public bool Unconfirmed { get { lock (stateGate) return unconfirmed || client?.State == SessionState.Unconfirmed; } }
    public CameraFacts? Facts { get; private set; }
    public Guid ConnectionId { get; private set; }
    public CameraBattery? Battery { get; private set; }
    public AstroPreparationReceipt? PreparationReceipt { get; private set; }
    public CameraGeometry? Geometry { get; private set; }
    public string Model => Facts?.Description?.Model ?? "Sony CrSDK Camera";
    public double PixelSizeMicrons => Settings.PixelSizeMicrons ??
        (CameraSupport.Normalize(Model) == "ILCE-7M5" && LastCapture?.RawFormat is not (5 or 6) ? 5.09 : double.NaN);
    /// <summary>User settings from settings.json; reloaded on every connect so edits need no rebuild.</summary>
    public PluginSettings Settings { get; private set; } = new();
    public event Action? Changed;

    private static string PluginDirectory =>
        Path.GetDirectoryName(typeof(SonyCameraService).Assembly.Location)
        ?? throw new InvalidOperationException("Plugin assembly has no directory");

    public async Task ConnectAsync(IImageDataFactory factory, CancellationToken token)
    {
        await SdkStartup.WaitAsync().WaitAsync(token).ConfigureAwait(false);
        using var connectionLifetime=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        token=connectionLifetime.Token;
        await EnterOperationAsync("连接相机", token).ConfigureAwait(false);
        try
        {
            if (Connected) return;
            deployment = PluginStorage.Current.LoadDeployment();
            Settings = PluginSettings.Load(PluginStorage.Current.SettingsDirectory());
            Settings.ValidateCameraSettings();
            Geometry = null;
            loader ??= new ArwImageLoader(factory);
            if (client is null)
            {
                var options = new NativeDeploymentOptions(deployment.ManifestPath, deployment.ApprovedManifestSha256);
                NativePackageValidator.Validate(options); // fail fast with a readable message
                client = new SonyCameraClient(options);
                client.Log += entry => Logger.Trace(entry.Operation + ": " + entry.Message);
            }
            // Initialization can fail before a connection is established; retries must finish it.
            await client.InitializeAsync().ConfigureAwait(false);
            await client.CheckFeatureAbiAsync().ConfigureAwait(false);
            if (client.State == SessionState.Connected && Facts is null)
                await client.DisconnectAsync().ConfigureAwait(false);
            // A pulled cable leaves the previous session unconfirmed; the native layer released it,
            // so clear the managed bookkeeping and let this connect re-prove the camera state.
            if (client.State == SessionState.Unconfirmed && client.State != SessionState.Connected)
            {
                try { await client.DisconnectAsync().ConfigureAwait(false); } catch (Exception ex) { Logger.Info("[SonyA7V] Disconnect before reconnect: " + ex.Message); }
                client.ResetForReconnect();
            }
            // A replugged camera needs a few seconds to enumerate; the SDK also sometimes needs a
            // fresh session before it can see a device that disappeared and came back.
            var cameras = await EnumerateWithRetryAsync(token).ConfigureAwait(false);
            if (cameras.Count == 0)
            {
                Logger.Info("[SonyA7V] No camera after retries; restarting the SDK session once");
                try { await client.RestartSdkAsync().ConfigureAwait(false); } catch (Exception ex) { Logger.Error("[SonyA7V] SDK restart failed: " + ex.Message, ex); }
                cameras = await EnumerateWithRetryAsync(token).ConfigureAwait(false);
            }
            var camera = CameraSupport.Select(cameras, Settings.CameraModel);
            token.ThrowIfCancellationRequested();
            await client.ConnectAsync(camera.Index).ConfigureAwait(false);
            PreparationReceipt = null;
            if (Settings.AstroPreparation is not null) {
                try {PreparationReceipt = await client.PrepareAstroSessionAsync(Settings.AstroPreparation).ConfigureAwait(false);}
                catch(AstroPreparationException failure) {
                    PreparationReceipt=failure.Receipt;
                    Logger.Error("[SonyCrSDK] Partial astronomy preparation receipt: " + JsonSerializer.Serialize(failure.Receipt.Changes),failure);
                    throw;
                }
            }
            ClearCorrection();
            // A clean SDK connection can stay open while the user corrects still-photo settings.
            try {await CheckCaptureReadyCoreAsync().ConfigureAwait(false);}
            catch(CameraCorrectionRequiredException) { /* Publish connected facts and a visible correction hint below. */ }
            var parameters = await client.ReadParametersAsync().ConfigureAwait(false);
            var isos = await client.GetSupportedIsoAsync().ConfigureAwait(false);
            var shutters = await client.GetSupportedExposureSecondsAsync().ConfigureAwait(false);
            var capabilities = await client.GetExposureCapabilitiesAsync().ConfigureAwait(false);
            if (isos.Count == 0 || shutters.Count == 0)
                throw new NotSupportedException($"{camera.Model} does not expose writable fixed ISO and ordinary still shutters in the current mode. Static RAW capture is required by NINA.");
            var preview = await client.GetPropertyAsync(CameraProperty.LiveViewStatus).ConfigureAwait(false);
            // SDK LiveViewStatus: Enable=2, Disable=1, NotSupport=0. Disabled may be enabled later.
            var liveAvailable = preview.Info is { Readable: true, Current: 1 or 2 };
            Directory.CreateDirectory(deployment.ReceiveRoot);
            token.ThrowIfCancellationRequested();
            // Publish ready state only after ALL connection steps succeed.
            ConnectionId = Guid.NewGuid();
            Facts = new CameraFacts(parameters, isos, shutters, capabilities, camera, liveAvailable);
            Geometry = CameraGeometry.Bootstrap(camera.Model, Settings);
            await RefreshBatteryCoreAsync().ConfigureAwait(false);
            active = null;
            ready = null;
            lock (stateGate) { last = null; unconfirmed = false; if(correctionHint.Length==0)lastError = ""; }
            Logger.Info($"[SonyA7V] Connected {camera.Model} ISO={parameters.Iso} shutter={parameters.ExposureSeconds}s bulbs={capabilities.BulbAvailable}");
        }
        catch (Exception ex)
        {
            lock (stateGate) {lastError = ex.Message;correctionHint="";}
            Logger.Error("[SonyA7V] Connect failed: " + ex.Message + " (if this repeats, unplug and replug the camera)", ex);
            Facts = null;
            ConnectionId = Guid.Empty;
            // SDK connection may have succeeded before readiness/parameter reads failed.
            // Release that partial connection, preserving the original error for the user.
            // Reuse the sole client; disposing it would prevent same-process retries.
            if (client?.State is SessionState.Connected or SessionState.Unconfirmed)
            {
                try { await client.DisconnectAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { Logger.Error("[SonyA7V] Failed connection cleanup: " + cleanup.Message, cleanup); }
            }
            lock (stateGate) unconfirmed = client?.State == SessionState.Unconfirmed;
            throw;
        }
        finally
        {
            ExitOperation();
            NotifyChanged();
        }
    }

    private async Task<IReadOnlyList<CameraDescription>> EnumerateWithRetryAsync(CancellationToken token)
    {
        IReadOnlyList<CameraDescription> last = Array.Empty<CameraDescription>();
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            last = await client!.EnumerateAsync().ConfigureAwait(false);
            if (last.Count > 0) { Logger.Info($"[SonyA7V] Camera found on enumeration attempt {attempt}"); return last; }
            await Task.Delay(800, token).ConfigureAwait(false);
        }
        return last;
    }
    public Task RefreshBatteryAsync() => CoordinateAsync("更新电量", RefreshBatteryCoreAsync);
    private async Task RefreshBatteryCoreAsync() {
        if(client is null)return;
        try { Battery = await client.GetBatteryAsync().ConfigureAwait(false); }
        catch(Exception ex) { Battery = null; Logger.Trace("[SonyA7V] Battery unavailable: " + ex.Message); }
    }
    private void RequireIdleSettings(bool allowPreview = false) {
        if(client is null || Facts is null || Unconfirmed || client.State!=SessionState.Connected || (!allowPreview && LiveViewActive) || active is {IsCompleted:false})
            throw new InvalidOperationException("Connect the camera and stop exposure/live view before changing astronomy settings");
    }
    public async Task<AstroPreparationReceipt> PrepareAstroAsync(AstroPreparationOptions options) {
        await EnterOperationAsync("应用天文准备").ConfigureAwait(false);
        try {
            RequireIdleSettings();
            if(PreparationReceipt?.Changes.Any(x=>x.Submitted)==true)
                throw new InvalidOperationException("Restore the existing preparation receipt before applying another plan");
            try {PreparationReceipt=await client!.PrepareAstroSessionAsync(options).ConfigureAwait(false);}
            catch(AstroPreparationException ex){PreparationReceipt=ex.Receipt;throw;}
            var parameters=await client!.ReadParametersAsync().ConfigureAwait(false);
            Facts=Facts! with{Parameters=parameters};
            return PreparationReceipt;
        }finally {ExitOperation();NotifyChanged();}
    }
    public async Task RestoreAstroAsync() {
        await EnterOperationAsync("恢复天文准备").ConfigureAwait(false);
        try {
            RequireIdleSettings();
            var receipt=PreparationReceipt??throw new InvalidOperationException("No preparation receipt in this connection");
            await client!.RestoreAstroSessionAsync(receipt).ConfigureAwait(false);
            PreparationReceipt=null;
            Facts=Facts! with{Parameters=await client.ReadParametersAsync().ConfigureAwait(false)};
        }finally {ExitOperation();NotifyChanged();}
    }
    public async Task<double> SetApertureAsync(double fNumber) {
        await EnterOperationAsync("调整镜头光圈").ConfigureAwait(false);
        try {
            RequireIdleSettings();
            var actual=await client!.SetApertureAsync(fNumber).ConfigureAwait(false);
            Facts=Facts! with{Parameters=await client.ReadParametersAsync().ConfigureAwait(false)};
            return actual;
        }finally {ExitOperation();NotifyChanged();}
    }
    public Task<LensFocusStatus> GetLensFocusAsync() => CoordinateAsync("查询镜头状态", async () => {
        RequireIdleSettings(); return await client!.GetLensFocusAsync().ConfigureAwait(false);
    });
    public async Task<LensFocusStatus> MoveLensFocusAsync(long position,CancellationToken token) {
        await EnterOperationAsync("移动镜头", token).ConfigureAwait(false);
        try {RequireIdleSettings();Interlocked.Exchange(ref lensMoving,1);NotifyChanged();return await client!.MoveLensFocusAsync(position,token).ConfigureAwait(false);}
        finally {Interlocked.Exchange(ref lensMoving,0);ExitOperation();NotifyChanged();}
    }
    public async Task DisconnectAsync()
    {
        await EnterOperationAsync("断开相机").ConfigureAwait(false);
        try
        {
            if (client is null) return;
            try { await client.DisconnectAsync().ConfigureAwait(false); }
            catch (CameraException ex) when (ex.Error.Code == ProjectCode.Cancelled)
            {
                // The native layer reports an interrupted capture; the session remains unconfirmed.
                lock (stateGate) unconfirmed = true;
                throw;
            }
            lock (stateGate) unconfirmed = client.State == SessionState.Unconfirmed;
        }
        finally
        {
            Facts = null;
            ConnectionId = Guid.Empty;
            Battery = null;
            PreparationReceipt = null;
            lock(stateGate)correctionHint="";
            Geometry = null;
            active = null;
            ready = null;
            lock (stateGate) last = null;
            ExitOperation();
            NotifyChanged();
        }
    }

    /// <summary>Only the final plugin teardown releases the native client.</summary>
    public async Task ShutdownAsync()
    {
        lifetime.Cancel(); // Cancel enumeration/retry waits before waiting for the coordinator.
        await EnterOperationAsync("释放相机会话").ConfigureAwait(false);
        try
        {
            if (client is null) return;
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger.Error("[SonyA7V] Dispose reported an unconfirmed camera", ex); }
            client = null;
            Facts = null;
            ConnectionId = Guid.Empty;
        }
        finally
        {
            ExitOperation();
            NotifyChanged();
        }
    }

    /// <summary>
    /// Starts one exposure and returns immediately with the running native task, so NINA's
    /// exposure and download phases can be observed separately.
    /// </summary>
    public async Task StartExposureAsync(double seconds, int gain, string prefix, CancellationToken token)
    {
        await EnterOperationAsync("提交曝光", token).ConfigureAwait(false);
        try { await StartExposureCoreAsync(seconds, gain, prefix, token).ConfigureAwait(false); }
        catch (ArgumentException ex) {
            var problem=RequireCorrection(ex,"曝光或 ISO 请求不适用于当前相机，请调整参数后重试。未自动断开或重拍。");
            ready?.TrySetException(problem);throw problem;
        }
        catch (Exception ex) { ready?.TrySetException(ex); throw; }
        finally {ExitOperation();}
    }

    private async Task StartExposureCoreAsync(double seconds, int gain, string prefix, CancellationToken token)
    {
        if (client is null || Facts is null) throw new InvalidOperationException("Camera is not connected");
        if (LiveViewActive) await client.StopLiveViewAsync().ConfigureAwait(false);
        RequireIdleSettings();
        if (Unconfirmed) throw new InvalidOperationException("The previous exposure left the camera unconfirmed; disconnect and reconnect: " + LastError);
        if (active is { IsCompleted: false }) throw new InvalidOperationException("An exposure is already running");
        // A rejected new request must never leave the previous exposure downloadable as its result.
        active=null;
        lock(stateGate)last=null;
        // Validate mode/focus/RAW before any ISO/shutter mutation or capture submission.
        await CheckCaptureReadyCoreAsync().ConfigureAwait(false);
        ClearCorrection();
        var isos = Facts.Isos;
        // NINA sends Gain = -1 when the profile has no gain preference; keep the camera value.
        var iso = gain > 0 ? (uint)gain : Facts.Parameters.Iso;
        if (!isos.Contains(iso))
            throw new ArgumentOutOfRangeException(nameof(gain), $"ISO {iso} is not offered by this camera right now");
        var request = ExposurePolicy.Create(seconds, Facts.Shutters, Facts.Capabilities);
        if (request.Mode == ExposureMode.Ordinary)
            await ApplyOrdinaryParametersAsync(iso, request.Seconds).ConfigureAwait(false);
        else
            await ApplyIsoAsync(iso).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var startedAt = DateTime.UtcNow;
        var requestedSeconds = seconds;
        lock (stateGate) last = null;
        ready ??= new TaskCompletionSource<CaptureSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<CaptureSnapshot>(s => {
            // NINA waits for the exposure only; the download has its own budget in DownloadExposure.
            if (s.IsPending && s.Exposure?.Phase is not (ExposurePhase.Downloading or ExposurePhase.Restoring or ExposurePhase.Terminal)) return;
            // Ordinary captures enter Downloading right after the 120 ms release, so report readiness
            // only once the requested exposure time has actually elapsed on the host clock.
            if (!s.IsPending && !s.IsSuccess) { ready?.TrySetResult(s); return; }
            if ((DateTime.UtcNow - startedAt).TotalSeconds + 0.05 < requestedSeconds) return;
            ready?.TrySetResult(s);
        });
        active = client.CaptureAsync(deployment!.ReceiveRoot, prefix, request, progress, cancellationToken: token);
        NotifyChanged();
        await Task.Yield();
    }

    /// <summary>
    /// Writes only what differs. The camera rejects property writes in some post-capture states, and the
    /// client quarantines the session on a failed write, so redundant writes are a real failure source.
    /// </summary>
    private async Task ApplyIsoAsync(uint iso)
    {
        if (Facts is not null && Facts.Parameters.Iso == iso && Facts.Parameters.IsoKind == IsoKind.Fixed) return;
        try
        {
            await client!.SetIsoAsync(iso).ConfigureAwait(false);
            if (Facts is not null) Facts = Facts with { Parameters = Facts.Parameters with { Iso = iso, IsoKind = IsoKind.Fixed } };
        }
        catch (CameraException ex)
        {
            throw new InvalidOperationException(
                $"相机当前不接受 ISO 写入（可能正在处理上一张或已休眠）：{ex.Error.Message}。请断开后重新连接。", ex);
        }
    }

    private async Task ApplyOrdinaryParametersAsync(uint iso, double seconds)
    {
        await ApplyIsoAsync(iso).ConfigureAwait(false);
        await ApplyShutterAsync(seconds).ConfigureAwait(false);
    }

    private async Task ApplyShutterAsync(double seconds) {
        if (Facts is not null && Math.Abs(Facts.Parameters.ExposureSeconds - seconds) < 1e-9) return;
        try
        {
            await client!.SetExposureSecondsAsync(seconds).ConfigureAwait(false);
            if (Facts is not null) Facts = Facts with { Parameters = Facts.Parameters with { ExposureSeconds = seconds } };
        }
        catch (CameraException ex)
        {
            throw new InvalidOperationException(
                $"相机当前不接受快门写入（可能正在处理上一张或已休眠）：{ex.Error.Message}。请断开后重新连接。", ex);
        }
    }
    /// <summary>Returns as soon as the exposure itself is finished; NINA times this separately from the download.</summary>
    public async Task WaitExposureReadyAsync(CancellationToken token)
    {
        var gate = ready ?? throw new InvalidOperationException("No exposure was started");
        var snapshot = await gate.Task.WaitAsync(token).ConfigureAwait(false);
        if (!snapshot.IsPending && !snapshot.IsSuccess)
        {
            lock (stateGate) { last = snapshot; lastError = snapshot.Failure.Message; unconfirmed = client?.State == SessionState.Unconfirmed; }
            NotifyChanged();
            throw new InvalidOperationException("Exposure failed: " + snapshot.Failure.Message);
        }
    }

    /// <summary>Waits for the native capture (exposure, download and settings restore) to finish.</summary>
    public async Task WaitAsync(CancellationToken token) {
        var running = active ?? throw new InvalidOperationException("No exposure was started");
        var connection = ConnectionId;
        // Do not hold the coordinator while waiting: cancellation must be able to enter it.
        CaptureSnapshot snapshot;
        try { snapshot = await running.WaitAsync(token).ConfigureAwait(false); }
        catch (Exception ex) {
            if (connection == ConnectionId) lock (stateGate) {lastError = ex.Message; unconfirmed = client?.State == SessionState.Unconfirmed;}
            NotifyChanged(); throw;
        }
        await EnterOperationAsync("确认曝光结果", token).ConfigureAwait(false);
        try {
            if (connection != ConnectionId || !ReferenceEquals(running, active))
                throw new InvalidOperationException("Camera connection or exposure changed before delivery");
            lock (stateGate) last = snapshot;
            if (snapshot.IsSuccess) await RefreshBatteryCoreAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            lock (stateGate) {lastError = ex.Message; unconfirmed = client?.State == SessionState.Unconfirmed;}
            throw;
        } finally {ExitOperation();}
    }

    public async Task AbortAsync()
    {
        await EnterOperationAsync("取消曝光并确认状态").ConfigureAwait(false);
        try {
        if (client is null) return;
        // NINA calls Abort after local decoding/metadata errors too. With no live capture and a
        // clean connected client there is nothing to cancel/reconcile; doing so quarantines good sessions.
        if (active is not {IsCompleted:false} && client.State==SessionState.Connected && !Unconfirmed) return;
        var snapshot = active is { IsCompleted: false } ? await client.CancelAsync().ConfigureAwait(false) : null;
        if (snapshot is not null) lock (stateGate) { last = snapshot; lastError = snapshot.Failure.Message; }
        // Ask the native layer for proof that the camera is idle again; otherwise the session stays
        // unconfirmed and no reconnect is possible in this process.
        try
        {
            var recovery = await client.ReconcileAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            lock (stateGate) unconfirmed = !recovery.Recovered;
            Logger.Info($"[SonyA7V] Abort reconciliation recovered={recovery.Recovered} state={client.State}");
        }
        catch (Exception ex)
        {
            lock (stateGate) unconfirmed = true;
            Logger.Error("[SonyA7V] Abort reconciliation failed: " + ex.Message, ex);
        }
        } finally {ExitOperation();}
    }

    public async Task EndEarlyAsync()
    {
        await EnterOperationAsync("提前结束曝光").ConfigureAwait(false);
        try {
        if (client is null) return;
        var snapshot = active is { IsCompleted: false } ? await client.EndExposureAsync().ConfigureAwait(false) : null;
        if (snapshot is not null) lock (stateGate) { last = snapshot; lastError = snapshot.Failure.Message; unconfirmed = true; }
        } finally {ExitOperation();}
    }

    public CaptureSnapshot? LastCapture { get { lock (stateGate) return last; } }

    public Task SetIsoAsync(uint iso) => CoordinateAsync("调整 ISO", async () => {
        RequireIdleSettings();
        if (!Facts!.Isos.Contains(iso)) throw new ArgumentOutOfRangeException(nameof(iso));
        await ApplyIsoAsync(iso).ConfigureAwait(false);
    });

    public Task SetExposureSecondsAsync(double seconds) => CoordinateAsync("调整普通快门", async () => {
        RequireIdleSettings();
        if (!Facts!.Shutters.Contains(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        await ApplyShutterAsync(seconds).ConfigureAwait(false);
    });

    public void FailExposureGate(Exception error) => ready?.TrySetException(error);
    public void PrepareExposureGate() => ready = new TaskCompletionSource<CaptureSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool LiveViewActive => client?.LiveViewEnabled == true;

    /// <summary>Stop, update and restart preview as one coordinated operation shared by all entry points.</summary>
    public Task ConfigureLiveViewAsync(uint? iso, double? seconds, CancellationToken token = default) =>
        CoordinateAsync("准备实时预览", async () => {
            RequireIdleSettings(allowPreview: true);
            if (Facts!.LiveViewAvailable != true) throw new NotSupportedException($"{Model} has no available live view");
            if (iso.HasValue && !Facts.Isos.Contains(iso.Value)) throw new ArgumentOutOfRangeException(nameof(iso));
            if (seconds.HasValue && !Facts.Shutters.Contains(seconds.Value)) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (LiveViewActive && !iso.HasValue && !seconds.HasValue) return;
            if (LiveViewActive) await client!.StopLiveViewAsync().ConfigureAwait(false);
            if (iso.HasValue) await ApplyIsoAsync(iso.Value).ConfigureAwait(false);
            if (seconds.HasValue) await ApplyShutterAsync(seconds.Value).ConfigureAwait(false);
            await client!.StartLiveViewAsync().ConfigureAwait(false);
        }, token);

    public Task StartLiveViewAsync() => ConfigureLiveViewAsync(null, null);

    public Task<LiveViewFrame?> GetLiveViewFrameAsync(CancellationToken token) =>
        CoordinateAsync("读取实时预览", () => (client ?? throw new InvalidOperationException("Camera is not connected")).GetLiveViewFrameAsync(token), token, announce: false);

    public Task StopLiveViewAsync() => CoordinateAsync("停止实时预览", async () => {
        if (client is not null && LiveViewActive) await client.StopLiveViewAsync().ConfigureAwait(false);
    });

    /// <summary>The only path that turns a completed capture into an image handed to NINA.</summary>
    public async Task<ArwImageFrame> LoadAsync(CancellationToken token)
    {
        await EnterOperationAsync("交付图像", token).ConfigureAwait(false);
        try {
        CaptureSnapshot? snapshot;
        lock (stateGate) snapshot = last;
        if (snapshot is null) throw new InvalidOperationException("No completed capture to download");
        if (!snapshot.IsSuccess || string.IsNullOrEmpty(snapshot.ArwPath))
            throw new InvalidOperationException("Capture was not successful: " + snapshot.Failure.Message);
        var info = new FileInfo(snapshot.ArwPath);
        if (!info.Exists) throw new FileNotFoundException("Camera reported a file that is not on disk", snapshot.ArwPath);
        if ((ulong)info.Length != snapshot.FileBytes)
            throw new InvalidDataException($"File size {info.Length} does not match the camera's reported {snapshot.FileBytes} bytes");
        var frame = await loader!.LoadAsync(snapshot.ArwPath, token, attachOriginalRaw: Settings.AttachOriginalRaw).ConfigureAwait(false);
        if (Facts?.Description is { } camera && CameraSupport.Normalize(frame.RawInfo.Model) != CameraSupport.Normalize(camera.Model))
            throw new InvalidDataException($"RAW model {frame.RawInfo.Model} does not match connected camera {camera.Model}");
        Geometry = CameraGeometry.FromRaw(frame.RawInfo);
        NotifyChanged();
        return frame;
        } catch(Exception ex) when(ex is not OperationCanceledException && LastCapture is {IsSuccess:true} && client?.State==SessionState.Connected && !Unconfirmed) {
            _=RequireCorrection(ex,"原片接收已结束，但本地图像处理失败。连接保留；请检查 RAW 设置后重试拍摄。失败原片保留用于排查："+(LastCapture?.ArwPath??"未知"));
            throw;
        } finally {ExitOperation();}
    }
}

/// <summary>Minimal IProgress adapter; the client reports on its owner thread.</summary>
internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
