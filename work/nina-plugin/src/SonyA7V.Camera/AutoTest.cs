using System.IO;
using System.Text.Json;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

namespace SonyA7V.Camera;

/// <summary>
/// Test-only end-to-end run inside an isolated NINA instance. Drives the real ICamera contract and
/// NINA's own prepare/save mediators, so the delivered file is produced by the host pipeline.
/// Environment: SONY_A7V_AUTOTEST=output dir, optional SONY_A7V_TEST_SECONDS, SONY_A7V_TEST_CANCEL_AFTER,
/// SONY_A7V_AUTOTEST_EXIT=1 (shut the host down gracefully when done).
/// Never exits the process abruptly: that would leave the Sony SDK session attached to the camera.
/// </summary>
internal static class AutoTest
{
    internal static void ScheduleIfRequested(IImageDataFactory factory, IImagingMediator imaging, IImageSaveMediator saving, IProfileService profiles)
    {
        var root = Environment.GetEnvironmentVariable("SONY_A7V_AUTOTEST");
        if (string.IsNullOrWhiteSpace(root)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await RunAsync(root, factory, imaging, saving, profiles).ConfigureAwait(false);
        });
    }

    private static double EnvDouble(string name, double fallback)
        => double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    private static async Task RunAsync(string root, IImageDataFactory factory, IImagingMediator imaging, IImageSaveMediator saving, IProfileService profiles)
    {
        Directory.CreateDirectory(root);
        var report = new Dictionary<string, object?> { ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["pid"] = Environment.ProcessId };
        var log = new List<string>();
        void Note(string s) { lock (log) log.Add($"{DateTimeOffset.Now:O} {s}"); Console.WriteLine("[AUTOTEST] " + s); }
        var camera = new SonyA7VCamera(SonyCameraService.Shared, factory, profiles);
        var seconds = EnvDouble("SONY_A7V_TEST_SECONDS", 1.0);
        var cancelAfter = EnvDouble("SONY_A7V_TEST_CANCEL_AFTER", 0);
        var frames = (int)EnvDouble("SONY_A7V_TEST_FRAMES", 1);
        try
        {
            Note("connecting");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 420));
            if (!await camera.Connect(cts.Token).ConfigureAwait(false)) throw new InvalidOperationException("Connect returned false");
            report["device"] = new { camera.DisplayName, camera.SensorName, camera.CameraXSize, camera.CameraYSize, camera.BitDepth, sensor = camera.SensorType.ToString(), driver = camera.DriverInfo };
            var gains = camera.Gains;
            var gain = gains.Contains(800) ? 800 : gains.Min();
            camera.Gain = gain;
            Note($"exposing {seconds}s at ISO {gain} (cancelAfter={cancelAfter})");
            var started = DateTime.UtcNow;
            camera.StartExposure(new CaptureSequence { ExposureTime = seconds, Gain = gain, ImageType = "LIGHT", Binning = new BinningMode(1, 1) });

            if (cancelAfter > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(cancelAfter), CancellationToken.None).ConfigureAwait(false);
                Note("aborting");
                camera.AbortExposure();
                var waitFailed = false;
                try { await camera.WaitUntilExposureIsReady(cts.Token).ConfigureAwait(false); }
                catch (Exception ex) { waitFailed = true; Note("wait after abort: " + ex.GetType().Name + ": " + ex.Message); }
                report["cancel"] = new { cancelAfter, waitFailed, stateAfterAbort = camera.CameraState.ToString() };
                var reconnectOk = false;
                try { camera.Disconnect(); Note("disconnect after abort ok"); } catch (Exception ex) { Note("disconnect after abort: " + ex.Message); }
                for (var attempt = 1; attempt <= 2 && !reconnectOk; attempt++)
                {
                    try
                    {
                        if (await camera.Connect(cts.Token).ConfigureAwait(false)) { reconnectOk = true; Note($"reconnect attempt {attempt} ok"); camera.Disconnect(); }
                    }
                    catch (Exception ex) { Note($"reconnect attempt {attempt} failed: {ex.Message}"); await Task.Delay(2000).ConfigureAwait(false); }
                }
                report["reconnectOk"] = reconnectOk;
                report["result"] = reconnectOk ? "PASS" : "FAIL";
                return;
            }

            var frameResults = new List<object>();
            for (var frame = 1; frame <= frames; frame++)
            {
            if (frame > 1)
            {
                camera.StartExposure(new CaptureSequence { ExposureTime = seconds, Gain = gain, ImageType = "LIGHT", Binning = new BinningMode(1, 1) });
                started = DateTime.UtcNow;
            }
            await camera.WaitUntilExposureIsReady(cts.Token).ConfigureAwait(false);
            Note($"frame {frame}/{frames} exposure ready; camera state=" + camera.CameraState);
            var exposure = await camera.DownloadExposure(cts.Token).ConfigureAwait(false);
            var data = await exposure.ToImageData(cancelToken: cts.Token).ConfigureAwait(false);
            var raw = data.Data.RAWData;
            var rawAttached = raw is { Length: > 0 };
            var sha = rawAttached ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)) : "";
            report["capture"] = new
            {
                seconds,
                gain,
                width = data.Properties.Width,
                height = data.Properties.Height,
                bitDepth = data.Properties.BitDepth,
                bayered = data.Properties.IsBayered,
                exposureDataBitDepth = exposure.BitDepth,
                arwBytes = raw?.Length ?? 0,
                arwSha256 = sha,
                cameraInfo = new
                {
                    bayer = data.MetaData.Camera.SensorType.ToString(),
                    pixelSize = data.MetaData.Camera.PixelSize,
                    exposureTime = data.MetaData.Image.ExposureTime,
                    gain = data.MetaData.Camera.Gain,
                    start = data.MetaData.Image.ExposureStart,
                    midpoint = data.MetaData.Image.ExposureMidPoint
                },
                elapsedSeconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 2)
            };
            Note($"download {raw?.Length ?? 0} bytes rawAttached={rawAttached} sha256={sha}");

            var saveRoot = Path.Combine(root, "saved");
            Directory.CreateDirectory(saveRoot);
            profiles.ActiveProfile.ImageFileSettings.FilePath = saveRoot;
            profiles.ActiveProfile.ImageFileSettings.FilePattern = "SONY_A7V_$$TARGETNAME$$";
            data.MetaData.Image.Id = Environment.TickCount & int.MaxValue;
            data.MetaData.Image.ImageType = "LIGHT";
            data.MetaData.Target.Name = "A7V_NINA_TEST";
            var prepared = await imaging.PrepareImage(data, new PrepareImageParameters(false, false), cts.Token).ConfigureAwait(false);
            var saved = await SaveAsync(saving, data, prepared, cts.Token).ConfigureAwait(false);
            var finalPath = saved.PathToImage.LocalPath;
            var savedBytes = await File.ReadAllBytesAsync(finalPath, cts.Token).ConfigureAwait(false);
            var savedSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(savedBytes));
            var matches = rawAttached && savedSha == sha && savedBytes.Length == raw!.Length;
            report["saved"] = new
            {
                path = finalPath,
                bytes = savedBytes.Length,
                sha256 = savedSha,
                rawAttached,
                matchesCapturedRaw = matches,
                fileType = saved.FileType.ToString(),
                isBayered = saved.IsBayered,
                imageWidth = saved.Image?.PixelWidth,
                imageHeight = saved.Image?.PixelHeight
            };
            Note($"frame {frame}/{frames} saved {finalPath} ({savedBytes.Length} bytes) rawAttached={rawAttached} match={matches}");
            if (rawAttached && !matches) throw new InvalidDataException("Host-saved ARW differs from the captured RAW bytes");
            if (!rawAttached && savedBytes.Length < (long)camera.CameraXSize * camera.CameraYSize) throw new InvalidDataException("Saved container is smaller than the raw pixel payload");
            frameResults.Add(new { frame, seconds, elapsed = Math.Round((DateTime.UtcNow - started).TotalSeconds, 2), path = finalPath, bytes = savedBytes.Length, sha256 = savedSha, fileType = saved.FileType.ToString(), width = data.Properties.Width, height = data.Properties.Height });
            }
            report["frames"] = frameResults;
            Note($"all {frames} frame(s) completed");
            camera.Disconnect();
            report["result"] = "PASS";
        }
        catch (Exception ex)
        {
            report["result"] = "FAIL";
            report["error"] = ex.ToString();
            Note("FAILED " + ex.Message);
        }
        finally
        {
            report["log"] = log;
            report["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O");
            try { File.WriteAllText(Path.Combine(root, "autotest-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Console.WriteLine("[AUTOTEST] cannot write report: " + ex.Message); }
            var exitCode = report["result"] as string == "PASS" ? 0 : 1;
            try { camera.Disconnect(); } catch { }
            try { await SonyCameraService.Shared.ShutdownAsync().ConfigureAwait(false); } catch (Exception ex) { Note("shutdown before exit failed: " + ex.Message); }
            if (Environment.GetEnvironmentVariable("SONY_A7V_AUTOTEST_EXIT") == "1")
            {
                try { System.Windows.Application.Current?.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown(exitCode)); }
                catch (Exception ex) { Note("graceful shutdown unavailable: " + ex.Message); }
            }
        }
    }

    private static async Task<ImageSavedEventArgs> SaveAsync(IImageSaveMediator saving, IImageData data, IRenderedImage rendered, CancellationToken token)
    {
        var done = new TaskCompletionSource<ImageSavedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ImageSavedEventArgs> handler = (_, e) =>
        {
            if (e.MetaData.Image.Id == data.MetaData.Image.Id) done.TrySetResult(e);
        };
        saving.ImageSaved += handler;
        try
        {
            token.ThrowIfCancellationRequested();
            await saving.Enqueue(data, Task.FromResult(rendered), new Progress<ApplicationStatus>(), token).ConfigureAwait(false);
            return await done.Task.WaitAsync(TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
        }
        finally { saving.ImageSaved -= handler; }
    }
}
