using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonyCamera.Client;

// 只读探测器：不改相机设置，只读取当前值、可读可写标志、候选/范围。
// 输出用于把项目属性 ID 与机身实际令牌对应起来（拍前门禁的语义依据），不是能力公告。
Console.OutputEncoding = Encoding.UTF8;
string Arg(string key, string fallback) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
var configPath = Path.GetFullPath(Arg("--config", CameraClientConfiguration.DefaultPath));
var run = Path.GetFullPath(Arg("--run-dir", Path.Combine(Path.GetTempPath(), $"probe-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}")));
Directory.CreateDirectory(run);
var json = new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
using var log = new StreamWriter(Path.Combine(run, "probe.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
void Write(string s) { var line = $"{DateTimeOffset.Now:O} {s}"; Console.WriteLine(line); log.WriteLine(line); }
void Save(string name, object value) => File.WriteAllText(Path.Combine(run, name), JsonSerializer.Serialize(value, json), new UTF8Encoding(false));

var configuration = CameraClientConfiguration.Load(configPath);
var deployment = configuration.ToDeploymentOptions();
var package = NativePackageValidator.Validate(deployment);
Write($"PACKAGE id={package.PackageId} backend={package.Backend} manifestSha={package.ManifestSha256}");
Save("package.json", package);

await using var client = new SonyCameraClient(deployment);
client.Log += e => log.WriteLine($"NATIVE {e.Operation}: {e.Message}");
await client.InitializeAsync();
Write($"ABI version={client.Abi!.Version}");
var cameras = await client.EnumerateAsync();
Save("cameras.json", cameras);
Write($"CAMERAS {string.Join(",", cameras.Select(c => $"{c.Index}:{c.Model}:{c.Connection}"))}");
if (cameras.Count == 0) { Write("NO CAMERA"); return 2; }
await client.ConnectAsync(cameras[0].Index);
await client.CheckFeatureAbiAsync();

var baseline = await client.ReadParametersAsync();
Save("baseline.json", baseline);
Write($"BASELINE iso={baseline.Iso} isoKind={baseline.IsoKind} shutterKind={baseline.ShutterKind} sec={baseline.ExposureSeconds} f={baseline.FNumber}");

var isos = await client.GetSupportedIsoAsync();
var shutters = await client.GetSupportedExposureSecondsAsync();
var caps = await client.GetExposureCapabilitiesAsync();
Save("capabilities.json", new { isos, shutters, caps });
Write($"ISO count={isos.Count} first={string.Join(",", isos.Take(6))} last={string.Join(",", isos.TakeLast(4))}");
Write($"SHUTTER count={shutters.Count} first={string.Join(",", shutters.Take(6))} last={string.Join(",", shutters.TakeLast(4))}");
Write($"CAPS bulb={caps.BulbAvailable} min={caps.BulbMinimumSeconds} max={caps.BulbMaximumSeconds} step={caps.BulbStepSeconds} timerReadable={caps.TimerReadable} timerEnabled={caps.TimerEnabled}");

var probes = await client.GetPropertiesAsync();
var rows = probes.Select(p => new {
    id = (int)p.Property,
    name = p.Property.ToString(),
    available = p.Available,
    readable = p.Info?.Readable,
    writable = p.Info?.Writable,
    current = p.Info?.Current,
    range = p.Info?.IsRange,
    min = p.Info?.Minimum,
    max = p.Info?.Maximum,
    step = p.Info?.Step,
    candidateCount = p.Info?.Candidates.Count,
    candidates = p.Info?.Candidates,
    errorCode = p.Error?.Code.ToString(),
    errorMessage = p.Error?.Message,
}).ToArray();
Save("properties.json", rows);
foreach (var r in rows)
    Write($"PROP {r.id,2} {r.name,-28} avail={r.available,-5} r={r.readable,-5} w={r.writable,-5} cur={(r.current?.ToString() ?? "-"),-8} " +
          (r.range == true ? $"range=[{r.min}..{r.max}] step={r.step}" : $"cands={r.candidateCount}:{string.Join(",", (r.candidates ?? Array.Empty<long>()).Take(24))}") +
          (r.available ? "" : $" err={r.errorCode}"));

var battery = await client.GetBatteryAsync();
Save("battery.json", battery);
Write($"BATTERY percent={battery.Percent?.ToString() ?? "null"} raw={battery.RawLevel?.ToString() ?? "null"} readable={battery.PercentReadable}");

var saveDest = await client.GetSaveDestinationAsync();
Save("save-destination.json", saveDest);
Write($"SAVE_DEST {saveDest}");
var wantDest = Arg("--save-dest", "");
if (wantDest.Length > 0) {
    var target = wantDest switch { "computer" => SaveDestination.Computer, "cardboth" => SaveDestination.ComputerAndCard, "card" => SaveDestination.Card, _ => throw new ArgumentException(wantDest) };
    var actual = await client.SetSaveDestinationAsync(target);
    Write($"SAVE_DEST set {target} -> readback {actual}");
}

var shutterArg = Arg("--shutter", "");
if (shutterArg.Length > 0) {
    var want = double.Parse(shutterArg, System.Globalization.CultureInfo.InvariantCulture);
    var actual = await client.SetExposureSecondsAsync(want);
    Write($"SHUTTER set {want} -> readback {actual}");
}

var focusArg = Arg("--focus-mode", "");
if (focusArg.Length > 0) {
    var want = long.Parse(focusArg, System.Globalization.CultureInfo.InvariantCulture);
    var actual = await client.SetPropertyAsync(CameraProperty.FocusMode, want);
    Write($"FOCUSMODE set {want} -> readback {actual}");
}

var capture = Arg("--capture", "");
if (capture.Length > 0) {
    var seconds = double.Parse(Arg("--seconds", "0.01"), System.Globalization.CultureInfo.InvariantCulture);
    var timeout = double.Parse(Arg("--timeout", "20"), System.Globalization.CultureInfo.InvariantCulture);
    var mode = capture == "bulb" ? ExposureMode.Bulb : ExposureMode.Ordinary;
    var receive = Path.Combine(run, "接收照片");
    Directory.CreateDirectory(receive);
    Write($"CAPTURE start mode={mode} seconds={seconds} timeout={timeout} destination={await client.GetSaveDestinationAsync()}");
    var progress = new InlineProgress<CaptureSnapshot>(s => Write($"PROGRESS id={s.CaptureId} state={s.State} code={s.Code} phase={s.Exposure?.Phase} hold={s.Exposure?.SoftwareHoldSeconds:F3}s cameraElapsed={s.Exposure?.CameraElapsedSeconds}"));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var snap = await client.CaptureAsync(receive, "PROBE", new ExposureRequest(mode, seconds, timeout, timeout), progress);
    Write($"CAPTURE done in {sw.Elapsed.TotalSeconds:F2}s success={snap.IsSuccess} state={snap.State} code={snap.Code} bytes={snap.FileBytes} path={snap.ArwPath} notifications={snap.DownloadNotifications}");
    Save("capture.json", snap);
    if (snap.IsSuccess) {
        using var fs = File.OpenRead(snap.ArwPath);
        Write($"CAPTURE sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs))} path={snap.ArwPath}");
    }
    if (client.State == SessionState.Unconfirmed) {
        Write("CAPTURE session unconfirmed; attempting reconnect for a clean state");
        try { await client.DisconnectAsync(); } catch (Exception ex) { Write($"DISCONNECT {ex.Message}"); }
    }
}

if (args.Contains("--list-contents")) {
    // Contents 模式只读枚举：确认快门是否真的把文件写进了卡。
    try { await client.DisconnectAsync(); } catch (Exception ex) { Write($"DISCONNECT-BEFORE-CONTENTS {ex.Message}"); }
    var idx = (await client.EnumerateAsync())[0].Index;
    await client.ConnectAsync(idx, CameraConnectionMode.Contents);
    var folders = await client.GetFoldersAsync();
    Save("folders.json", folders);
    Write($"FOLDERS {folders.Count}: {string.Join(",", folders.Select(f => $"{f.Name}(h={f.Handle},s={f.Session})"))}");
    foreach (var folder in folders.Take(2)) {
        var items = await client.GetContentsAsync(folder);
        Save($"contents-{folder.Name}.json", items);
        Write($"CONTENTS {folder.Name} n={items.Count}");
        foreach (var item in items.OrderByDescending(x => x.Name).Take(8))
            Write($"  FILE {item.Name,-16} bytes={item.Bytes,-10} {item.Width}x{item.Height} date={item.CameraDate}");
    }
    try { await client.DisconnectAsync(); } catch (Exception ex) { Write($"DISCONNECT-AFTER-CONTENTS {ex.Message}"); }
}

await client.DisconnectAsync();
await client.DisposeAsync();
Write("PROBE COMPLETE (read-only; no camera setting changed)");
return 0;

sealed class InlineProgress<T>(Action<T> action):IProgress<T> { public void Report(T value)=>action(value); }
