using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using SonyA7V.Camera;
using SonyCamera.Client;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Supply the NINA app directory; prepare an isolated TEST manifest beside this executable first.");
        var host = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(host, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        await Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var manifest = Path.Combine(AppContext.BaseDirectory, "native", "approved-native.json");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));
        // TEST packages are explicitly allowed only in this isolated test process.
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest).TrimStart('\uFEFF'));
        Check(document.RootElement.GetProperty("backend").GetString() == "test", "Refuse a real SDK package");
        var client = new SonyCameraClient(new NativeDeploymentOptions(manifest, hash, AllowTestPackage: true));
        var service = new SonyCameraService(()=>new CameraDeployment{ManifestPath=manifest,ApprovedManifestSha256=hash,ReceiveRoot=Path.Combine(AppContext.BaseDirectory,"receive")},()=>new());
        typeof(SonyCameraService).GetField("client", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, client);
        var saved = Environment.GetEnvironmentVariable("SW_FAKE_CASE");
        var savedModel = Environment.GetEnvironmentVariable("SW_FAKE_MODEL_A7V");
        try
        {
            await client.InitializeAsync();
            Environment.SetEnvironmentVariable("SW_FAKE_MODEL_A7V", "1");
            foreach (var scenario in new[] { "feature-not-manual", "feature-af-blocks" })
            {
                Environment.SetEnvironmentVariable("SW_FAKE_CASE", scenario);
                await service.ConnectAsync(null!, CancellationToken.None);
                Check(service.Connected && service.Facts is not null, "Correctable readiness error keeps the session connected");
                Check(service.CorrectionHint.Length>0, "Correction is visible");
                var sameConnection=service.ConnectionId;
                Environment.SetEnvironmentVariable("SW_FAKE_CASE","feature-normal");
                await service.RecheckReadyAsync();
                Check(service.ConnectionId==sameConnection && service.CorrectionHint.Length==0,"Correct settings without reconnecting");
                await service.DisconnectAsync();
            }
            Environment.SetEnvironmentVariable("SW_FAKE_CASE", "feature-normal");
            await service.ConnectAsync(null!, CancellationToken.None);
            Check(service.Connected && service.Facts is not null, "Retry succeeds in the SAME process/client");
            await service.DisconnectAsync();
            Check(!service.Connected && service.Facts is null, "Disconnect clears readiness facts");
            await service.ConnectAsync(null!, CancellationToken.None);
            Check(service.Connected && service.Facts is not null, "Reconnect reloads facts");
            await service.SetIsoAsync(800);
            Environment.SetEnvironmentVariable("SW_FAKE_BODY_ISO","1600");
            service.PrepareExposureGate();
            await service.StartExposureAsync(0.01,800,"ISO",CancellationToken.None);
            await service.WaitAsync(CancellationToken.None);
            Check(service.LastCapture?.ActualParameters?.Iso==800,"Body-side ISO changes are corrected despite cached 800");
            Environment.SetEnvironmentVariable("SW_FAKE_BODY_ISO","400");
            service.PrepareExposureGate();
            await service.StartExposureAsync(0.01,-1,"ISO",CancellationToken.None);
            await service.WaitAsync(CancellationToken.None);
            Check(service.LastCapture?.ActualParameters?.Iso==400,"Unspecified gain uses fresh body ISO");
            // Retention policy: delivered frames are cleaned, failures and foreign paths are kept.
            var receiveRoot=Path.Combine(AppContext.BaseDirectory,"receive");
            var staged=Path.Combine(receiveRoot,"capture-test0001-p1");
            Directory.CreateDirectory(staged);
            var stagedRaw=Path.Combine(staged,"TEST00001.ARW");
            File.WriteAllBytes(stagedRaw,new byte[64]);
            var delivered=new CaptureSnapshot(ProjectCode.Ok,CaptureState.Succeeded,1,null,0,SaveDestination.Computer,0,64,new CameraError(ProjectCode.Ok,0,0,0,""),staged,stagedRaw,null);
            service.CompleteDelivery(delivered);
            Check(!File.Exists(stagedRaw)&&!Directory.Exists(staged),"successful delivery removes the staged RAW and its empty folder");
            var keptDir=Path.Combine(receiveRoot,"capture-test0002-p1");
            Directory.CreateDirectory(keptDir);
            var keptRaw=Path.Combine(keptDir,"TEST00002.ARW");
            File.WriteAllBytes(keptRaw,new byte[64]);
            service.CompleteDelivery(delivered with{State=CaptureState.Failed,ArwPath=keptRaw,ReceiveDirectory=keptDir});
            Check(File.Exists(keptRaw)&&Directory.Exists(keptDir),"failed capture keeps its RAW for diagnosis");
            var outside=Path.Combine(AppContext.BaseDirectory,"outside-capture");
            Directory.CreateDirectory(outside);
            var outsideRaw=Path.Combine(outside,"TEST00003.ARW");
            File.WriteAllBytes(outsideRaw,new byte[64]);
            service.CompleteDelivery(delivered with{ArwPath=outsideRaw,ReceiveDirectory=outside});
            Check(File.Exists(outsideRaw),"cleanup refuses paths outside the receive root");
            Directory.Delete(outside,true);
            await service.DisconnectAsync();
            Environment.SetEnvironmentVariable("SW_FAKE_CASE","connect-timeout");
            try{await service.ConnectAsync(null!,CancellationToken.None);throw new Exception("Timeout expected");}catch(CameraException ex){Check(ex.Error.Code==ProjectCode.Timeout,"Connection timeout reported");}
            Environment.SetEnvironmentVariable("SW_FAKE_CASE","feature-normal");
            await service.ConnectAsync(null!,CancellationToken.None);
            Check(service.Connected,"Timeout recovers on same client and process");
            Console.WriteLine("PASS: correctable readiness, body-side ISO changes, unspecified gain, timeout and same-client reconnect.");
        }
        finally
        {
            await service.ShutdownAsync();
            Environment.SetEnvironmentVariable("SW_FAKE_CASE", saved);
            Environment.SetEnvironmentVariable("SW_FAKE_MODEL_A7V", savedModel);
        }
    }
}
