using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using SonyA7V.Camera;
using SonyCamera.Client;

internal static class Program {
    static int assertions;
    static int realRawPipelineFiles;
    static void Check(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
    static void Reject<T>(Action action, string message) where T:Exception {
        try { action(); } catch(T) { assertions++; return; } throw new Exception(message);
    }
    static async Task Main(string[] args) {
        if (args.Length != 1) throw new ArgumentException("NINA app path required");
        var host = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            var path = Path.Combine(host, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        await Run();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task Run() {
        var caps = new ExposureCapabilities(true,1,900,.001,1800,3600,false,false);
        foreach(var s in new[]{.01,1.0/60,30.0,60.0})
            Check(ExposurePolicy.Create(s,new[]{s},caps).Mode==ExposureMode.Ordinary,"exact candidate must win, including 60s");
        Check(ExposurePolicy.Create(2, new[]{.01}, caps).Mode==ExposureMode.Bulb,"Bulb below 30 seconds");
        Check(ExposurePolicy.Create(900,new[]{.01},caps).Mode==ExposureMode.Bulb,"Bulb upper policy boundary");
        Check(ExposurePolicy.Maximum(new[]{.01,60.0},caps with{BulbAvailable=false})==60,"no invented Bulb maximum");
        Reject<NotSupportedException>(()=>ExposurePolicy.Create(2,new[]{.01},caps with{BulbAvailable=false}),"unsupported bulb");
        foreach(var s in new[]{0,-1,double.NaN,double.PositiveInfinity,.1,900.001,1.0000001})
            Reject<ArgumentOutOfRangeException>(()=>ExposurePolicy.Create(s,new[]{.01},caps),"invalid exposure rejected");
        Reject<ArgumentOutOfRangeException>(()=>ExposurePolicy.Create(1.0/60+1e-12,new[]{1.0/60},caps),"do not round a near shutter");
        var camera = new CameraDescription(0,"ILCE-6700","FAKE");
        Check(CameraSupport.Select(new[]{camera},null)==camera,"single body selection");
        Check(CameraSupport.Select(new[]{camera,new CameraDescription(1,"ILCE-7M5","FAKE")},"ilce-6700")==camera,"explicit model selection");
        Reject<InvalidOperationException>(()=>CameraSupport.Select(new[]{camera,camera},null),"never select first camera");
        Reject<InvalidOperationException>(()=>CameraSupport.Select(new[]{camera},"ILCE-7M5"),"selector mismatch");
        Reject<InvalidDataException>(()=>new PluginSettings{SensorWidth=10}.ValidateCameraSettings(),"partial dimensions rejected");
        Reject<InvalidDataException>(()=>new PluginSettings{PixelSizeMicrons=double.NaN}.ValidateCameraSettings(),"invalid pitch rejected");
        Reject<InvalidDataException>(()=>new PluginSettings{BayerPattern="RGB"}.ValidateCameraSettings(),"invalid CFA rejected");
        foreach(var cfa in new[]{"RGGB","BGGR","GRBG","GBRG"}) {
            var info=new RawInfo{Width=6000,Height=4000,RawWidth=6048,RawHeight=4020,Pitch=12096,Cfa=cfa};
            Check(CameraGeometry.FromRaw(info)==new CameraGeometry(6000,4000,cfa),"active RAW geometry and CFA");
            Reject<InvalidDataException>(()=>CameraGeometry.FromRaw(info with{Width=uint.MaxValue}),"wide geometry arithmetic");
        }
        var inventory=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"camera-models.json")));
        Check(inventory.RootElement.GetProperty("models").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(CameraSupport.SdkModels),"deployment inventory matches runtime SDK catalog");
        Check(CameraGeometry.Bootstrap("ILCE-6700",new())==null,"unknown geometry is not A7M5");
        Check(CameraGeometry.Bootstrap("ILCE-7M5",new())?.Width==7028,"preserve measured A7M5 bootstrap");

        var manifest=Path.Combine(AppContext.BaseDirectory,"native","approved-native.json");
        using(var json=JsonDocument.Parse(File.ReadAllBytes(manifest)))
            Check(json.RootElement.GetProperty("backend").GetString()=="test","REFUSE production SDK in offline tests");
        var options=new NativeDeploymentOptions(manifest,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))),true);
        Check(NativePackageValidator.Validate(options).Backend=="test","multi-model test manifest validates with hashes");
        Reject<InvalidDataException>(()=>NativePackageValidator.Validate(options with{AllowTestPackage=false}),"production refuses test manifest");
        Reject<InvalidDataException>(()=>NativePackageValidator.Validate(options with{ApprovedManifestSha256=new string('0',64)}),"hash pin is mandatory");
        foreach(var modelJson in new[]{"[]","[\"ILCE-7M5\",\"ilce-7m5\"]","[\" \"]"}) {
            var modified=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!;
            modified["supportedModels"]=System.Text.Json.Nodes.JsonNode.Parse(modelJson);
            var testManifest=Path.Combine(Path.GetDirectoryName(manifest)!,"invalid-models.json");
            File.WriteAllText(testManifest,modified.ToJsonString());
            var testOptions=options with{ManifestPath=testManifest,ApprovedManifestSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testManifest)))};
            var rejected=false;
            try{NativePackageValidator.Validate(testOptions);}catch(InvalidDataException ex){rejected=ex.Message.Contains("supportedModels");}
            Check(rejected,"empty/duplicate/blank model inventory is refused before file checks");
            File.Delete(testManifest);
        }
        var client=new SonyCameraClient(options);
        var service=new SonyCameraService();
        typeof(SonyCameraService).GetField("client",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(service,client);
        var driver=new SonyA7VCamera(service,null!);
        async Task Connect(string scenario,string model="ILCE-6700") {
            Environment.SetEnvironmentVariable("SW_FAKE_CASE",scenario);
            Environment.SetEnvironmentVariable("SW_FAKE_MODEL",model);
            await service.ConnectAsync(null!,CancellationToken.None);
        }
        async Task RejectAsync<T>(Func<Task> action,string name) where T:Exception {
            try {await action();}catch(T){assertions++;return;}throw new Exception(name);
        }
        try {
            foreach(var model in CameraSupport.SdkModels) {
                await Connect("feature-normal",model);
                Check(service.Connected && service.Model==model,"connect identity: "+model);
                Check(driver.Name==model && driver.SensorName==model,"NINA identity: "+model);
                Check(driver.CameraXSize==(model=="ILCE-7M5"?7028:0),"no foreign geometry: "+model);
                Check(model=="ILCE-7M5"?driver.PixelSizeX==5.09:double.IsNaN(driver.PixelSizeX),"no foreign pitch: "+model);
                Check(driver.CanShowLiveView,"live preview advertised by fake SDK");
                await service.DisconnectAsync();
                Check(service.Geometry is null && service.LastCapture is null,"disconnect clears prior model metadata");
                Console.WriteLine("SIMULATED MODEL PASS "+model);
            }
            // Real wrapper/client APIs backed only by isolated fake core.
            var prep=new AstroPreparationOptions{DisableLongExposureNoiseReduction=true,DisableHighIsoNoiseReduction=true,DisableSteadyShot=true,FullSizeLosslessRaw=true,ManualLensFocus=true};
            await Connect("feature-normal","ILCE-7M5");
            Check(driver.HasBattery&&driver.BatteryLevel==78,"NINA shows confirmed battery percentage");
            Check(driver.HasSetupDialog,"camera exposes native NINA setup entry");
            var ui=await service.ReadUiPropertiesAsync();Check(ui.Count==AstroUiCatalog.Definitions.Count,"real wrapper UI property batch");
            var connection=service.ConnectionId;
            await RejectAsync<InvalidOperationException>(()=>service.ApplyUiPropertyAsync(Guid.NewGuid(),CameraProperty.LongExposureNoiseReduction,2,1),"stale UI connection cannot write");
            await RejectAsync<InvalidOperationException>(()=>service.ApplyUiPropertyAsync(connection,CameraProperty.LongExposureNoiseReduction,1,2),"stale UI current cannot write");
            Check(await service.ApplyUiPropertyAsync(connection,CameraProperty.LongExposureNoiseReduction,2,1)==1,"UI write readback through real wrapper");
            Check(await service.ApplyUiPropertyAsync(connection,CameraProperty.LongExposureNoiseReduction,1,2)==2,"UI restores original NR");
            await RejectAsync<NotSupportedException>(()=>service.ApplyUiPropertyAsync(connection,CameraProperty.Slot1RemainingImages,0,1),"UI card counter read-only");
            Check(ui.All(x=>x.Property!=CameraProperty.FocusMagnifier),"magnifier is absent from plugin property surface");
            await RejectAsync<NotSupportedException>(()=>service.ApplyUiPropertyAsync(connection,CameraProperty.FocusMagnifier,0xFFFFFFFF,0x28FFFFFFFF),"removed magnifier cannot bypass UI whitelist");
            var coordinator=(SemaphoreSlim)typeof(SonyCameraService).GetField("opGate",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(service)!;
            await coordinator.WaitAsync();
            var coordinatedCalls=new Task[]{service.SetIsoAsync(800),service.SetExposureSecondsAsync(.01),service.RefreshBatteryAsync(),service.GetLensFocusAsync(),service.EndEarlyAsync()};
            try{await Task.Delay(30);Check(coordinatedCalls.All(x=>!x.IsCompleted),"ISO shutter battery lens and controls share one service coordinator");}finally{coordinator.Release();}
            await Task.WhenAll(coordinatedCalls);
            Action badObserver=()=>throw new InvalidOperationException("test observer");service.Changed+=badObserver;
            try{await service.SetIsoAsync(800);Check(service.UiSnapshot.CanEdit,"observer exceptions cannot strand the coordinator");}finally{service.Changed-=badObserver;}
            await service.ConfigureLiveViewAsync(400,1.0/60);
            Check(service.LiveViewActive&&service.Facts!.Parameters.Iso==400&&service.Facts.Parameters.ExposureSeconds==1.0/60,"preview update is one stop-write-start service operation");
            await RejectAsync<InvalidOperationException>(()=>service.SetIsoAsync(800),"plain ISO mutation cannot interrupt preview");
            await service.ConfigureLiveViewAsync(800,.01);
            Check(service.LiveViewActive&&service.Facts!.Parameters.Iso==800,"preview reconfiguration returns stream to running state");
            await RejectAsync<ArgumentOutOfRangeException>(()=>service.ConfigureLiveViewAsync(999999,null),"invalid preview request rejected before stopping stream");Check(service.LiveViewActive,"invalid request preserves active preview");
            await service.StopLiveViewAsync();
            await service.PrepareAstroAsync(new(){DisableLongExposureNoiseReduction=true});
            await RejectAsync<InvalidOperationException>(()=>service.ApplyUiPropertyAsync(connection,CameraProperty.LongExposureNoiseReduction,1,2),"receipt-owned property protected");
            await service.RestoreAstroAsync();
            await client.StartLiveViewAsync();
            await RejectAsync<InvalidOperationException>(()=>client.InspectPropertiesAsync(new[]{CameraProperty.Aperture}),"UI inspection refuses active preview");
            await RejectAsync<InvalidOperationException>(()=>client.SetPropertyIfCurrentAsync(CameraProperty.LongExposureNoiseReduction,2,1),"UI writes refuse active preview");
            await client.StopLiveViewAsync();
            Check(JsonDocument.Parse(driver.Action("Sony.GetAstroStatus","")).RootElement.GetProperty("batteryPercent").GetInt32()==78,"NINA structured astronomy status");
            Reject<JsonException>(()=>driver.Action("Sony.PrepareAstroSession","{\"disableLongExposureNoiseReducton\":true}"),"typo in preparation is refused before write");
            Check(driver.Action("Sony.SetAperture","4")=="4","exact NINA lens aperture action");
            Reject<CameraException>(()=>driver.Action("Sony.SetAperture","3.5"),"NINA aperture never rounds to another candidate");
            driver.Action("Sony.PrepareAstroSession","{\"disableLongExposureNoiseReduction\":true}");
            Reject<InvalidOperationException>(()=>driver.Action("Sony.PrepareAstroSession","{}"),"do not overwrite an existing settings receipt");
            Check(driver.Action("Sony.RestoreAstroSession","")=="restored","NINA explicit same-session restore action");
            var receipt=await client.PrepareAstroSessionAsync(prep);
            Check(receipt.Changes.Count==5&&receipt.Changes.Count(x=>x.Submitted)==4,"preparation records exact changes; existing MF is not rewritten");
            Check((await client.GetPropertyAsync(CameraProperty.RawCompression)).Info?.Current==2,"lossless RAW semantic target");
            var same=await client.PrepareAstroSessionAsync(prep);
            Check(same.Changes.All(x=>!x.Submitted),"idempotent preparation submits no redundant writes");
            await client.RestoreAstroSessionAsync(receipt);
            Check((await client.GetPropertyAsync(CameraProperty.LongExposureNoiseReduction)).Info?.Current==2,"explicit restore returns original NR");
            await RejectAsync<InvalidOperationException>(()=>client.RestoreAstroSessionAsync(receipt),"receipt cannot restore twice");
            var lens=await client.GetLensFocusAsync();Check(!lens.Driving&&lens.Position==0&&lens.Target.Maximum==65535,"absolute lens capability readback");
            lens=await client.MoveLensFocusAsync(12345);Check(lens.Position==12345&&!lens.Driving,"absolute focus completes with idle proof");
            await RejectAsync<ArgumentOutOfRangeException>(()=>client.MoveLensFocusAsync(65536),"focus target out of range");
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();await RejectAsync<OperationCanceledException>(()=>client.MoveLensFocusAsync(1,cancelled.Token),"cancel before lens motion");}
            var stale=await client.PrepareAstroSessionAsync(new());
            var conflict=await client.PrepareAstroSessionAsync(new(){DisableLongExposureNoiseReduction=true});
            await client.SetPropertyAsync(CameraProperty.LongExposureNoiseReduction,2);
            await RejectAsync<InvalidOperationException>(()=>client.RestoreAstroSessionAsync(conflict),"restore does not overwrite a subsequent camera/user change");
            await service.DisconnectAsync();
            Check(!driver.HasBattery&&driver.BatteryLevel==-1,"disconnect clears battery");
            await Connect("feature-normal");
            await RejectAsync<InvalidOperationException>(()=>client.RestoreAstroSessionAsync(stale),"receipt cannot cross camera reconnect");
            await client.StartLiveViewAsync();
            await RejectAsync<InvalidOperationException>(()=>client.PrepareAstroSessionAsync(prep),"preparation refused during preview");
            await client.StopLiveViewAsync();await service.DisconnectAsync();
            foreach(var mode in new[]{"astro-unsupported","astro-readonly"}) {
                await Connect(mode);
                await RejectAsync<NotSupportedException>(()=>client.PrepareAstroSessionAsync(prep),"preflight fails before any write");
                Check((await client.GetPropertyAsync(CameraProperty.LongExposureNoiseReduction)).Info?.Current==2,"all-target preflight leaves earlier settings unchanged");
                await service.DisconnectAsync();
            }
            await Connect("astro-query-error");
            await RejectAsync<CameraException>(()=>client.GetPropertyAsync(CameraProperty.HighIsoNoiseReduction),"transient SDK error is not unsupported");
            await service.DisconnectAsync();
            await Connect("astro-lossless-large");
            var large=await client.PrepareAstroSessionAsync(new(){FullSizeLosslessRaw=true});
            Check(large.Changes.Single().Requested==5,"full-size lossless L fallback; never substitute S/M");
            await service.DisconnectAsync();
            await Connect("astro-partial");
            try {await client.PrepareAstroSessionAsync(prep);throw new Exception("partial write must fail");}
            catch(AstroPreparationException ex){Check(ex.Receipt.Changes.Count(x=>x.Submitted)==2&&ex.Receipt.Changes.Count(x=>x.Submitted&&x.Confirmed)==1&&client.State==SessionState.Unconfirmed,"partial write receipt and quarantine");}
            await service.DisconnectAsync();
            foreach(var mode in new[]{"battery-unknown","battery-absent"}) {
                await Connect(mode);Check(!driver.HasBattery&&driver.BatteryLevel==-1,"unknown battery remains unknown: "+mode);await service.DisconnectAsync();
            }
            await Connect("focus-busy");
            await RejectAsync<InvalidOperationException>(()=>client.MoveLensFocusAsync(100),"busy lens blocks a second motion");await service.DisconnectAsync();
            // NINA opt-in preparation also handles AF->MF before capture readiness, defaults do not.
            var settingsPath=Path.Combine(AppContext.BaseDirectory,"settings.json");
            File.WriteAllText(settingsPath,JsonSerializer.Serialize(new PluginSettings{EnableLensFocuser=true,AstroPreparation=new(){ManualLensFocus=true,DisableLongExposureNoiseReduction=true}}));
            try {
                await Connect("feature-af-blocks");Check(service.PreparationReceipt?.Changes.Count==2,"NINA uses explicit preparation before readiness");
                var focuser=new SonyLensFocuser(service);Check(await focuser.Connect(CancellationToken.None),"NINA lens focuser attaches shared session");
                Check(focuser.Position==0&&focuser.MaxStep==65535&&double.IsNaN(focuser.StepSize)&&!focuser.TempCompAvailable,"NINA focuser reports SDK units and unknown physical scale");
                await focuser.Move(400,CancellationToken.None,0);Check(focuser.Position==400&&!focuser.IsMoving,"NINA absolute focuser move readback");
                focuser.Halt();focuser.Disconnect();Check(service.Connected&&!focuser.Connected,"detaching lens focuser preserves imager connection");
                await focuser.Connect(CancellationToken.None);
                await service.DisconnectAsync();
                await Connect("feature-normal");
                Check(!focuser.Connected&&focuser.Position==-1,"focuser cannot inherit position after camera reconnect");
                await service.DisconnectAsync();
            }
            finally {File.WriteAllText(settingsPath,"{}");}
            File.WriteAllText(settingsPath,JsonSerializer.Serialize(new PluginSettings{AstroPreparation=new(){DisableLongExposureNoiseReduction=true,DisableHighIsoNoiseReduction=true}}));
            try {
                await RejectAsync<AstroPreparationException>(()=>Connect("astro-partial"),"NINA connect preparation failure is explicit");
                Check(!service.Connected&&service.PreparationReceipt?.Changes.Count(x=>x.Submitted)==2,"NINA preserves partial settings receipt after failed connect cleanup");
            }finally {File.WriteAllText(settingsPath,"{}");}
            await Connect("feature-normal");
            await RejectAsync<InvalidOperationException>(()=>new SonyLensFocuser(service).Connect(CancellationToken.None),"lens focuser remains opt-in");
            await service.DisconnectAsync();
            await Connect("feature-no-lens");
            await RejectAsync<NotSupportedException>(()=>client.GetLensFocusAsync(),"telescope-only rig does not fabricate lens focus");
            await service.DisconnectAsync();
            await Connect("feature-normal");
            service.PrepareExposureGate();await service.StartExposureAsync(2,800,"COORD_CANCEL",CancellationToken.None);
            var completion=service.WaitAsync(CancellationToken.None);
            await RejectAsync<InvalidOperationException>(()=>service.SetIsoAsync(400),"service ISO edit refuses active capture");
            await RejectAsync<InvalidOperationException>(()=>service.ReadUiPropertiesAsync(),"panel cannot inspect mutation candidates during exposure");
            await service.AbortAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.CurrentOperation is null,"waiting for exposure does not lock out abort or strand coordinator");
            await RejectAsync<InvalidOperationException>(()=>service.WaitExposureReadyAsync(CancellationToken.None),"cancelled capture is never delivered as ready success");
            try{await service.DisconnectAsync();}catch(CameraException ex)when(ex.Error.Code==ProjectCode.Cancelled){}
            await Connect("feature-normal");Check(service.UiSnapshot.CanEdit,"clean reconnect usable after coordinated cancellation");await service.DisconnectAsync();
            foreach(var token in new[]{0,1,2,3,4,5,6}) {
                Environment.SetEnvironmentVariable("SW_FAKE_RAW_FORMAT",token.ToString());
                await Connect("feature-normal");
                service.PrepareExposureGate();
                await service.StartExposureAsync(.01,800,"OFFLINE",CancellationToken.None);
                await service.WaitAsync(CancellationToken.None);
                Check(service.LastCapture?.IsSuccess==true,"RAW compression token "+token);
                await service.DisconnectAsync();
            }
            Environment.SetEnvironmentVariable("SW_FAKE_RAW_FORMAT","999");
            var unknownRawRejected=false;
            try {await Connect("feature-normal");}catch(CameraException){unknownRawRejected=true;}
            Check(!unknownRawRejected&&service.Connected&&service.CorrectionHint.Length>0,"unknown RAW token blocks capture but retains correction session");
            Environment.SetEnvironmentVariable("SW_FAKE_RAW_FORMAT","1");await service.RecheckReadyAsync();Check(service.CorrectionHint.Length==0,"RAW format correction does not need reconnect");await service.DisconnectAsync();
            Environment.SetEnvironmentVariable("SW_FAKE_RAW_FORMAT",null);
            await Connect("feature-normal");var decodeConnection=service.ConnectionId;
            service.PrepareExposureGate();await service.StartExposureAsync(.01,800,"BAD_LOCAL",CancellationToken.None);await service.WaitAsync(CancellationToken.None);
            await RejectAsync<InvalidDataException>(()=>service.LoadAsync(CancellationToken.None),"malformed local RAW not delivered");
            await service.AbortAsync();Check(service.Connected&&!service.Unconfirmed&&service.ConnectionId==decodeConnection&&service.CorrectionHint.Length>0,"decode failure plus host abort keeps clean camera session");
            await service.RecheckReadyAsync();Check(service.Connected&&service.CorrectionHint.Length==0,"camera reusable after a local decode failure");
            service.PrepareExposureGate();await RejectAsync<CameraCorrectionRequiredException>(()=>service.StartExposureAsync(.01,int.MaxValue,"BAD_ISO",CancellationToken.None),"invalid requested ISO rejected before capture");
            await service.AbortAsync();Check(service.Connected&&service.LastCapture is null,"rejected new request clears previous capture slot without disconnect");
            await RejectAsync<InvalidOperationException>(()=>service.WaitAsync(CancellationToken.None),"rejected request cannot wait on previous capture");
            await RejectAsync<InvalidOperationException>(()=>service.LoadAsync(CancellationToken.None),"rejected request cannot download previous photo");
            await service.DisconnectAsync();
            // Real NINA image factory + real pinned LibRaw bridge; fake transport uses a clearly
            // synthetic DNG copied as the received RAW. This is pipeline evidence, never ARW evidence.
            var detector=DispatchProxy.Create<NINA.Core.Interfaces.IPluggableBehaviorSelector<NINA.Image.ImageAnalysis.IStarDetection>,ImageBehaviorProxy>();
            var annotator=DispatchProxy.Create<NINA.Core.Interfaces.IPluggableBehaviorSelector<NINA.Image.ImageAnalysis.IStarAnnotator>,ImageBehaviorProxy>();
            var profile=DispatchProxy.Create<NINA.Profile.Interfaces.IProfileService,TestProfileProxy>();
            var imageFactory=new NINA.Image.ImageData.ImageDataFactory(profile,detector,annotator);
            typeof(SonyCameraService).GetField("loader",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(service,new ArwImageLoader(imageFactory));
            await Connect("feature-normal");
            foreach(var cfa in new[]{"RGGB","BGGR","GRBG","GBRG"}) {
                var fixture=Path.Combine(AppContext.BaseDirectory,"synthetic-raw",cfa+".dng");
                Environment.SetEnvironmentVariable("SW_FAKE_RAW_FIXTURE",fixture);
                service.PrepareExposureGate();
                await service.StartExposureAsync(.01,800,"SYNTHETIC",CancellationToken.None);
                await service.WaitAsync(CancellationToken.None);
                var frame=await service.LoadAsync(CancellationToken.None);
                Check(frame.ImageData.Data.FlatArray.Length==96*80 && frame.RawInfo.Cfa==cfa,"NINA image plane "+cfa);
                Check(service.Geometry==new CameraGeometry(96,80,cfa),"service learns active geometry "+cfa);
                Check(driver.CameraXSize==96 && driver.CameraYSize==80 && driver.SensorType.ToString()==cfa,"NINA properties follow RAW "+cfa);
                Check(frame.MetaData.Camera.BayerPattern.ToString()==cfa,"metadata follows actual CFA "+cfa);
                Check(frame.ImageData.Data.RAWData is null,"default FITS pixels do not attach camera container");
                var preserved=await new ArwImageLoader(imageFactory).LoadAsync(fixture,attachOriginalRaw:true);
                Check(preserved.ImageData.Data.RAWData?.Length==new FileInfo(fixture).Length,"optional original bytes retained");
            }
            await service.DisconnectAsync();
            await Connect("feature-normal","ILCE-7M5");
            service.PrepareExposureGate();
            await service.StartExposureAsync(.01,800,"MISMATCH",CancellationToken.None);
            await service.WaitAsync(CancellationToken.None);
            var mismatched=false;
            try {await service.LoadAsync(CancellationToken.None);} catch(InvalidDataException){mismatched=true;}
            Check(mismatched,"wrong-model RAW must not be delivered");
            await service.DisconnectAsync();
            Environment.SetEnvironmentVariable("SW_FAKE_RAW_FIXTURE",null);
            var corpusPath=Environment.GetEnvironmentVariable("SW_REAL_CORPUS_INDEX");
            if(!string.IsNullOrEmpty(corpusPath)) {
                using var corpus=JsonDocument.Parse(File.ReadAllText(corpusPath));
                foreach(var sample in corpus.RootElement.EnumerateArray()) {
                    Check(sample.GetProperty("verified").GetBoolean(),"real input must have a verified source hash");
                    var path=sample.GetProperty("path").GetString()!;
                    var model=sample.GetProperty("model").GetString()!;
                    var expectedHash=sample.GetProperty("sha256").GetString()!;
                    Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(expectedHash,StringComparison.OrdinalIgnoreCase),"source RAW SHA256");
                    var token=path.Contains("raw_modes",StringComparison.OrdinalIgnoreCase)?"2":path.Contains("HQ",StringComparison.OrdinalIgnoreCase)||path.Contains("-HD",StringComparison.OrdinalIgnoreCase)?"6":"1";
                    Environment.SetEnvironmentVariable("SW_FAKE_RAW_FORMAT",token);
                    Environment.SetEnvironmentVariable("SW_FAKE_RAW_FIXTURE",path);
                    await Connect("feature-normal",model);
                    service.PrepareExposureGate();
                    await service.StartExposureAsync(.01,800,"REALRAW",CancellationToken.None);
                    await service.WaitAsync(CancellationToken.None);
                    var frame=await service.LoadAsync(CancellationToken.None);
                    Check(service.LastCapture?.IsSuccess==true&&service.LastCapture.FileBytes==(ulong)new FileInfo(path).Length,"fake transport completes full real RAW");
                    Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(service.LastCapture!.ArwPath))).Equals(expectedHash,StringComparison.OrdinalIgnoreCase),"received real RAW is byte-identical to public input");
                    Check(frame.RawInfo.Model==model&&frame.RawInfo.Cfa=="RGGB","NINA receives actual Sony model and CFA");
                    Check(frame.ImageData.Data.FlatArray.LongLength==(long)frame.RawInfo.Width*frame.RawInfo.Height,"NINA receives complete Bayer array");
                    if(sample.TryGetProperty("expectedGeometry",out var geometry)) {
                        Check(frame.RawInfo.Width==geometry.GetProperty("width").GetUInt32()&&frame.RawInfo.Height==geometry.GetProperty("height").GetUInt32(),"R6 NINA dimensions match independent camera crop metadata");
                    }
                    if(sample.TryGetProperty("expectedPixelSha256",out var pixelHash)) {
                        var actualPixelHash=Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.ImageData.Data.FlatArray.AsSpan())));
                        Check(actualPixelHash.Equals(pixelHash.GetString(),StringComparison.OrdinalIgnoreCase),"NINA pixel array matches reviewed native decoder regression fingerprint");
                    }
                    Check(driver.CameraXSize==frame.RawInfo.Width&&driver.CameraYSize==frame.RawInfo.Height,"NINA runtime camera geometry follows this real RAW");
                    await service.DisconnectAsync();realRawPipelineFiles++;
                    Console.WriteLine("REAL RAW / SIMULATED TRANSPORT PASS "+Path.GetFileName(path));
                    GC.Collect();GC.WaitForPendingFinalizers();
                }
                Environment.SetEnvironmentVariable("SW_FAKE_RAW_FORMAT",null);
                Environment.SetEnvironmentVariable("SW_FAKE_RAW_FIXTURE",null);
            }
            await Connect("no-bulb");
            Check(!service.Facts!.Capabilities.BulbAvailable && driver.ExposureMax==60,"no Bulb reports only actual shutter limit");
            foreach(var shutter in new[]{60.0,1.0/60}) {
                service.PrepareExposureGate();
                await service.StartExposureAsync(shutter,800,"ORDINARY",CancellationToken.None);
                await service.WaitAsync(CancellationToken.None);
                Check(service.LastCapture?.IsSuccess==true && service.LastCapture.Exposure?.Mode==ExposureMode.Ordinary && service.LastCapture.ActualParameters?.ExposureSeconds==shutter,"native ordinary exact candidate including long/fractional shutter");
            }
            await service.DisconnectAsync();
            foreach(var mode in new[]{"legacy","legacy-absent","optional-absent","timer-readonly-off"}) {
                await Connect(mode);
                if(mode=="legacy") Check(!driver.CanShowLiveView,"legacy missing LiveView is unsupported");
                service.PrepareExposureGate();
                await service.StartExposureAsync(1,800,"LEGACY",CancellationToken.None);
                await service.WaitAsync(CancellationToken.None);
                Check(service.LastCapture?.IsSuccess==true,"legacy Bulb without timer: "+mode);
                Check(service.LastCapture?.Exposure?.RestoreState==2,"legacy shutter restored: "+mode);
                await service.DisconnectAsync();
            }
            foreach(var mode in new[]{"required-missing","optional-error","optional-unreadable"}) {
                var rejected=false;
                try { await Connect(mode); } catch(CameraException) { rejected=true; }
                Check(rejected && !service.Connected && service.Facts==null,"bad/unsupported state refused and cleaned: "+mode);
                await Connect("feature-normal");
                Check(service.Connected,"retry after "+mode);
                await service.DisconnectAsync();
            }
            foreach(var mode in new[]{"optional-wrong","video-only","feature-af-blocks","feature-not-manual"}) {
                await Connect(mode);var sameConnection=service.ConnectionId;
                Check(service.Connected&&!service.Unconfirmed&&service.CorrectionHint.Length>0,"correctable startup mode retains clean session: "+mode);
                service.PrepareExposureGate();await RejectAsync<CameraCorrectionRequiredException>(()=>service.StartExposureAsync(.01,800,"PRECHECK",CancellationToken.None),"bad mode stops before a trigger");
                await service.AbortAsync();Check(service.Connected&&!service.Unconfirmed&&service.ConnectionId==sameConnection,"NINA cleanup preserves rejected preflight session");
                if(mode=="feature-af-blocks")await service.ApplyUiPropertyAsync(sameConnection,CameraProperty.FocusMode,3,1);
                else Environment.SetEnvironmentVariable("SW_FAKE_CASE","feature-normal");
                await service.RecheckReadyAsync();Check(service.CorrectionHint.Length==0&&service.ConnectionId==sameConnection,"corrected setting rechecked without reconnect: "+mode);
                service.PrepareExposureGate();await service.StartExposureAsync(.01,800,"CORRECTED",CancellationToken.None);await service.WaitAsync(CancellationToken.None);
                Check(service.LastCapture?.IsSuccess==true&&service.Connected,"corrected session captures next photo: "+mode);await service.DisconnectAsync();
            }
            foreach(var timerFailure in new[]{"timer-error","timer-unreadable","timer-readonly-on"}) {
            await Connect(timerFailure);
            service.PrepareExposureGate();
            await service.StartExposureAsync(1,800,"TIMERERROR",CancellationToken.None);
            await service.WaitAsync(CancellationToken.None);
            Check(service.LastCapture?.IsSuccess==false,"transient timer error must never become absent/off");
            try { await service.DisconnectAsync(); }
            catch(CameraException ex) { Check(ex.Error.Code==ProjectCode.Cancelled,"failed task remains explicitly unconfirmed at disconnect"); }
            Check(!service.Connected && client.State==SessionState.Unconfirmed,"failure cannot masquerade as idle");
            await Connect("feature-normal");
            await service.DisconnectAsync();
            }
            var gate=(SemaphoreSlim)typeof(SonyCameraService).GetField("opGate",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(service)!;
            await gate.WaitAsync();
            var pendingConnect=service.ConnectAsync(null!,CancellationToken.None);
            var closing=service.ShutdownAsync();
            bool cancelledConnect=false;
            try{await pendingConnect.WaitAsync(TimeSpan.FromSeconds(2));}catch(OperationCanceledException){cancelledConnect=true;}
            finally{gate.Release();}
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Check(cancelledConnect&&!service.Connected,"shutdown cancels queued connect before waiting for the shared coordinator");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"summary.json"),JsonSerializer.Serialize(new {passed=true,assertions,simulatedModels=CameraSupport.SdkModels,realRawPipelineFiles,hardwareValidated=false}));
            Console.WriteLine($"PASS: {assertions} assertions; 32 SIMULATED models; {realRawPipelineFiles} real RAWs through SIMULATED transport. NO HARDWARE VALIDATION.");
        } finally {
            await service.ShutdownAsync();
            foreach(var name in new[]{"SW_FAKE_CASE","SW_FAKE_MODEL","SW_FAKE_RAW_FORMAT","SW_FAKE_RAW_FIXTURE"})Environment.SetEnvironmentVariable(name,null);
        }
    }
}

// Pixel creation does not render or detect stars. These selectors provide only the
// behavior/analysis objects required by NINA's constructor; image implementation stays real.
public class ImageBehaviorProxy:DispatchProxy {
    protected override object? Invoke(MethodInfo? method,object?[]? args) =>
        method?.Name is "GetBehavior" or "CreateAnalysis" ? DispatchProxy.Create(method.ReturnType,typeof(ImageBehaviorProxy)) : throw new NotSupportedException(method?.Name);
}
public class TestProfileProxy:DispatchProxy {
    private readonly NINA.Profile.Profile profile=new();
    protected override object? Invoke(MethodInfo? method,object?[]? args) =>
        method?.Name=="get_ActiveProfile" ? profile : throw new NotSupportedException(method?.Name);
}