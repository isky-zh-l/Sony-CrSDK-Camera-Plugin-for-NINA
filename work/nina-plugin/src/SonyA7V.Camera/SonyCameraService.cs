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
    ExposureCapabilities Capabilities);

/// <summary>
/// One process, one native session, one owner thread. NINA's connect/expose/download calls are
/// serialized here; the managed client already pins every native call to its own thread.
/// </summary>
public sealed class SonyCameraService
{
    private readonly SemaphoreSlim opGate = new(1, 1);
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
    /// <summary>User settings from settings.json; reloaded on every connect so edits need no rebuild.</summary>
    public PluginSettings Settings { get; private set; } = new();
    public event Action? Changed;

    private static string PluginDirectory =>
        Path.GetDirectoryName(typeof(SonyCameraService).Assembly.Location)
        ?? throw new InvalidOperationException("Plugin assembly has no directory");

    public async Task ConnectAsync(IImageDataFactory factory, CancellationToken token)
    {
        await opGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Connected) return;
            deployment = CameraDeployment.Load(PluginDirectory);
            Settings = PluginSettings.Load(PluginDirectory);
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
            if (cameras.Count != 1)
                throw new InvalidOperationException(
                    $"未发现可连接的 Sony 相机（找到 {cameras.Count} 台）。请确认：USB 已插好、相机已开机、没有被 Imaging Edge 等程序占用；刚插拔过请等几秒再试。");
            if (!cameras[0].Model.Contains("ILCE-7M5", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This plugin was verified against ILCE-7M5 only; found " + cameras[0].Model);
            token.ThrowIfCancellationRequested();
            await client.ConnectAsync(cameras[0].Index).ConfigureAwait(false);
            await client.CheckCaptureReadyAsync().ConfigureAwait(false);
            var parameters = await client.ReadParametersAsync().ConfigureAwait(false);
            var isos = await client.GetSupportedIsoAsync().ConfigureAwait(false);
            var shutters = await client.GetSupportedExposureSecondsAsync().ConfigureAwait(false);
            var capabilities = await client.GetExposureCapabilitiesAsync().ConfigureAwait(false);
            Directory.CreateDirectory(deployment.ReceiveRoot);
            token.ThrowIfCancellationRequested();
            // Publish ready state only after ALL connection steps succeed.
            Facts = new CameraFacts(parameters, isos, shutters, capabilities);
            lock (stateGate) { unconfirmed = false; lastError = ""; }
            Logger.Info($"[SonyA7V] Connected {cameras[0].Model} ISO={parameters.Iso} shutter={parameters.ExposureSeconds}s bulbs={capabilities.BulbAvailable}");
        }
        catch (Exception ex)
        {
            lock (stateGate) lastError = ex.Message;
            Logger.Error("[SonyA7V] Connect failed: " + ex.Message + " (if this repeats, unplug and replug the camera)", ex);
            Facts = null;
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
            opGate.Release();
            Changed?.Invoke();
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
    public async Task DisconnectAsync()
    {
        await opGate.WaitAsync().ConfigureAwait(false);
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
            opGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Only the final plugin teardown releases the native client.</summary>
    public async Task ShutdownAsync()
    {
        await opGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (client is null) return;
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger.Error("[SonyA7V] Dispose reported an unconfirmed camera", ex); }
            client = null;
            Facts = null;
        }
        finally
        {
            opGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Starts one exposure and returns immediately with the running native task, so NINA's
    /// exposure and download phases can be observed separately.
    /// </summary>
    public async Task StartExposureAsync(double seconds, int gain, string prefix, CancellationToken token)
    {
        try { await StartExposureCoreAsync(seconds, gain, prefix, token).ConfigureAwait(false); }
        catch (Exception ex) { ready?.TrySetException(ex); throw; }
    }

    private async Task StartExposureCoreAsync(double seconds, int gain, string prefix, CancellationToken token)
    {
        if (client is null || Facts is null) throw new InvalidOperationException("Camera is not connected");
        if (Unconfirmed) throw new InvalidOperationException("The previous exposure left the camera unconfirmed; disconnect and reconnect: " + LastError);
        if (active is { IsCompleted: false }) throw new InvalidOperationException("An exposure is already running");
        var isos = Facts.Isos;
        // NINA sends Gain = -1 when the profile has no gain preference; keep the camera value.
        var iso = gain > 0 ? (uint)gain : Facts.Parameters.Iso;
        if (!isos.Contains(iso))
            throw new ArgumentOutOfRangeException(nameof(gain), $"ISO {iso} is not offered by this camera right now");
        if (Math.Abs(seconds) < 1e-9 || !double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), "Exposure time must be a positive finite number");
        ExposureRequest request;
        if (seconds <= 30)
        {
            // The native contract requires the request to equal the read-back shutter exactly.
            if (!Facts.Shutters.Any(s => Math.Abs(s - seconds) < 1e-9))
                throw new ArgumentOutOfRangeException(nameof(seconds),
                    $"Exposure {seconds}s is not an exact camera shutter candidate. Pick one of the offered values.");
            await ApplyOrdinaryParametersAsync(iso, seconds).ConfigureAwait(false);
            request = new ExposureRequest(ExposureMode.Ordinary, seconds, seconds + 15, 180);
        }
        else
        {
            if (seconds > Facts.Capabilities.BulbMaximumSeconds || seconds < Facts.Capabilities.BulbMinimumSeconds)
                throw new ArgumentOutOfRangeException(nameof(seconds),
                    $"Bulb exposure must be within {Facts.Capabilities.BulbMinimumSeconds}..{Facts.Capabilities.BulbMaximumSeconds} seconds");
            if (Math.Abs(seconds * 1000 - Math.Round(seconds * 1000)) > 1e-6)
                throw new ArgumentOutOfRangeException(nameof(seconds), "Bulb exposure must be an exact millisecond value");
            await ApplyIsoAsync(iso).ConfigureAwait(false);
            request = new ExposureRequest(ExposureMode.Bulb, seconds, seconds + 30, 240);
        }
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
        Changed?.Invoke();
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
            Changed?.Invoke();
            throw new InvalidOperationException("Exposure failed: " + snapshot.Failure.Message);
        }
    }

    /// <summary>Waits for the native capture (exposure, download and settings restore) to finish.</summary>
    public async Task WaitAsync(CancellationToken token)
    {
        var running = active;
        if (running is null) throw new InvalidOperationException("No exposure was started");
        try
        {
            var snapshot = await running.WaitAsync(token).ConfigureAwait(false);
            lock (stateGate) { last = snapshot; }
        }
        catch (Exception ex)
        {
            lock (stateGate) { lastError = ex.Message; unconfirmed = client?.State == SessionState.Unconfirmed; }
            throw;
        }
        finally { Changed?.Invoke(); }
    }

    public async Task AbortAsync()
    {
        if (client is null) return;
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
        Changed?.Invoke();
    }

    public async Task EndEarlyAsync()
    {
        if (client is null) return;
        var snapshot = active is { IsCompleted: false } ? await client.EndExposureAsync().ConfigureAwait(false) : null;
        if (snapshot is not null) lock (stateGate) { last = snapshot; lastError = snapshot.Failure.Message; unconfirmed = true; }
        Changed?.Invoke();
    }

    public CaptureSnapshot? LastCapture { get { lock (stateGate) return last; } }

    // Parameter writes used by live view as well as stills: the camera keeps the values until changed.
    public async Task SetIsoAsync(uint iso)
    {
        var c = client ?? throw new InvalidOperationException("Camera is not connected");
        var actual = await c.SetIsoAsync(iso).ConfigureAwait(false);
        if (actual != iso) throw new InvalidOperationException($"Camera kept ISO {actual} instead of {iso}");
    }

    public async Task SetExposureSecondsAsync(double seconds)
    {
        var c = client ?? throw new InvalidOperationException("Camera is not connected");
        var actual = await c.SetExposureSecondsAsync(seconds).ConfigureAwait(false);
        if (Math.Abs(actual - seconds) > 1e-9) throw new InvalidOperationException($"Camera kept shutter {actual}s instead of {seconds}s");
    }
    // Live view: the native side owns start/stop/read; this layer only orders it against exposures.
    /// <summary>Creates the readiness gate synchronously so NINA's immediate Wait call cannot race it.</summary>
    /// <summary>Fails the readiness gate so NINA sees the real error instead of waiting out its timeout.</summary>
    public void FailExposureGate(Exception error) => ready?.TrySetException(error);

    public void PrepareExposureGate() => ready = new TaskCompletionSource<CaptureSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool LiveViewActive => client?.LiveViewEnabled == true;

    public async Task StartLiveViewAsync()
    {
        var c = client ?? throw new InvalidOperationException("Camera is not connected");
        if (active is { IsCompleted: false }) throw new InvalidOperationException("Cannot start live view during an exposure");
        await c.StartLiveViewAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task<LiveViewFrame?> GetLiveViewFrameAsync(CancellationToken token)
    {
        var c = client ?? throw new InvalidOperationException("Camera is not connected");
        return await c.GetLiveViewFrameAsync(token).ConfigureAwait(false);
    }

    public async Task StopLiveViewAsync()
    {
        if (client is null) return;
        try { await client.StopLiveViewAsync().ConfigureAwait(false); }
        catch (Exception ex) { Logger.Error("[SonyA7V] StopLiveView failed", ex); throw; }
        finally { Changed?.Invoke(); }
    }

    /// <summary>The only path that turns a completed capture into an image handed to NINA.</summary>
    public async Task<ArwImageFrame> LoadAsync(CancellationToken token)
    {
        CaptureSnapshot? snapshot;
        lock (stateGate) snapshot = last;
        if (snapshot is null) throw new InvalidOperationException("No completed capture to download");
        if (!snapshot.IsSuccess || string.IsNullOrEmpty(snapshot.ArwPath))
            throw new InvalidOperationException("Capture was not successful: " + snapshot.Failure.Message);
        var info = new FileInfo(snapshot.ArwPath);
        if (!info.Exists) throw new FileNotFoundException("Camera reported a file that is not on disk", snapshot.ArwPath);
        if ((ulong)info.Length != snapshot.FileBytes)
            throw new InvalidDataException($"File size {info.Length} does not match the camera's reported {snapshot.FileBytes} bytes");
        return await loader!.LoadAsync(snapshot.ArwPath, token, attachOriginalRaw: Settings.AttachOriginalRaw).ConfigureAwait(false);
    }
}

/// <summary>Minimal IProgress adapter; the client reports on its owner thread.</summary>
internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
