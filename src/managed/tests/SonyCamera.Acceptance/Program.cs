using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonyCamera.Client;

Console.OutputEncoding = Encoding.UTF8;
string Arg(string key, string fallback) { var i=Array.IndexOf(args,key); return i>=0&&i+1<args.Length?args[i+1]:fallback; }
var mode=Arg("--mode","inspect");
var fake=args.Contains("--fake");
var configPath=Path.GetFullPath(Arg("--config",CameraClientConfiguration.DefaultPath));
var run=Path.GetFullPath(Arg("--run-dir",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SonyNinaCrSdk","regression", $"{mode}-{DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)):yyyyMMdd-HHmmss-fff}")));
Directory.CreateDirectory(run);
var receive=Path.Combine(run,"接收照片"); Directory.CreateDirectory(receive);
using var log=new StreamWriter(Path.Combine(run,"acceptance.log"),false,new UTF8Encoding(false)){AutoFlush=true};
var gate=new object();
void Write(string s) {lock(gate){var line=$"{DateTimeOffset.Now:O} {s}"; Console.WriteLine(line); log.WriteLine(line);}}
void Need(bool pass,string text) {Write($"CHECK {(pass?"PASS":"FAIL")} {text}"); if(!pass)throw new InvalidOperationException(text);}
var json=new JsonSerializerOptions {WriteIndented=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals};
var captures=new List<CaptureSnapshot>();
var samples=new List<object>();
var threadIds=new HashSet<int>();
NativeDeploymentOptions deployment;
try {
 var configuration=CameraClientConfiguration.Load(configPath);
 if(configuration.AllowTestPackage!=fake)throw new InvalidDataException("测试后端必须同时使用测试批准配置和显式--fake；正式模式拒绝测试配置");
 deployment=configuration.ToDeploymentOptions();
 var package=NativePackageValidator.Validate(deployment);
 File.WriteAllText(Path.Combine(run,"package-validation.json"),JsonSerializer.Serialize(package,json),new UTF8Encoding(false));
 Write($"PACKAGE PASS id={package.PackageId} backend={package.Backend} manifest={package.ManifestPath} approved={package.ManifestSha256}");
 if(mode=="validate") {Write("VALIDATION PASS (read-only; no DLL loading or camera calls)");return 0;}
}catch(Exception e){Write($"DEPLOYMENT FAIL {e}");File.WriteAllText(Path.Combine(run,"deployment-failure.json"),JsonSerializer.Serialize(new{success=false,error=e.Message,exception=e.GetType().FullName},json));return 1;}
var native=Path.GetDirectoryName(deployment.ManifestPath)!;
var client=new SonyCameraClient(deployment);
client.Log+=entry=>{lock(gate) threadIds.Add(entry.OwnerThreadId);Write($"NATIVE thread={entry.OwnerThreadId} {entry.Operation}: {entry.Message}");};
CameraParameters? baseline=null;
SaveDestination? originalSave=null;
var success=false;var restoreNeeded=false;var expectedUnconfirmedDispose=false;
void Save(string name,object value)=>File.WriteAllText(Path.Combine(run,name),JsonSerializer.Serialize(value,json),new UTF8Encoding(false));
try {
 Write($"BACKEND={(fake?"FAKE":"REAL")} MODE={mode} native={native} run={run}");
 if(!fake)Need(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SW_FAKE_CASE")),"real mode has no fake environment");
 await client.InitializeAsync();
 Need(client.Abi is {Version:2},"runtime ABI2 and managed layout checks");
 Save("abi.json",client.Abi!);
 if(mode=="abi") {success=true;}
 else {
  var cameras=await client.EnumerateAsync();Save("cameras.json",cameras);
  Need(cameras.Count==1,"exactly one camera");
  Need((cameras[0].Model=="SIMULATED")==fake,"camera backend identity");
  if(!fake)Need(cameras[0].Model.Contains("ILCE-7M5",StringComparison.OrdinalIgnoreCase),"expected A7 V camera");
  await client.ConnectAsync(cameras[0].Index);
  baseline=await client.ReadParametersAsync();originalSave=await client.GetSaveDestinationAsync();
  Save("baseline.json",new {parameters=baseline,saveDestination=originalSave});
  var isos=await client.GetSupportedIsoAsync();var shutters=await client.GetSupportedExposureSecondsAsync();var cap=await client.GetExposureCapabilitiesAsync();
  Save("capabilities.json",new {isos,shutters,cap});
  Need(isos.Count>0&&shutters.Count>0,"real supported parameter arrays");
  await client.CheckCaptureReadyAsync();
  if(mode!="inspect") {
   Need(baseline.IsoKind==IsoKind.Fixed&&baseline.ShutterKind==ShutterKind.Seconds&&baseline.ExposureSeconds<=1,"baseline is fixed ISO and ordinary short shutter");
   restoreNeeded=true;
   if(mode!="fault") {
    var trial=isos.Contains(400u)&&baseline.Iso!=400?400u:isos.First(x=>x!=baseline.Iso);
    Need(await client.SetIsoAsync(trial)==trial,"set ISO readback");
    var trialShutter=shutters.First(x=>x>0&&x<=1&&x!=baseline.ExposureSeconds);
    Need(await client.SetExposureSecondsAsync(trialShutter)==trialShutter,"set ordinary shutter exact readback");
    Need(await client.SetExposureSecondsAsync(baseline.ExposureSeconds)==baseline.ExposureSeconds,"restore ordinary shutter");
    Need(await client.SetIsoAsync(baseline.Iso)==baseline.Iso,"restore ISO");
   }
   var invalidRejected=false;
   try{await client.SetExposureSecondsAsync(double.NaN);}catch(CameraException){invalidRejected=true;}catch(ArgumentException){invalidRejected=true;}
   Need(invalidRejected,"NaN ordinary shutter rejected");
   var afterInvalid=await client.ReadParametersAsync();Need(afterInvalid==baseline,"invalid setter leaves parameters unchanged");
   foreach(var invalidSeconds in new[]{double.NaN,double.PositiveInfinity,0d,-1d,900.001,1.0000001}) {
    var rejected=false;
    try{await client.CaptureAsync(receive,"INVALID",new(ExposureMode.Bulb,invalidSeconds,10));}
    catch(CameraException e){rejected=e.Error.Code is ProjectCode.Argument or ProjectCode.Unsupported&&e.Error.CaptureId==0;}
    Need(rejected&&client.State==SessionState.Connected,"invalid Bulb duration rejected without allocating task or quarantining camera");
   }
   using(var alreadyCancelled=new CancellationTokenSource()) {
    alreadyCancelled.Cancel();var rejected=false;
    try{await client.CaptureAsync(receive,"INVALID",new(ExposureMode.Bulb,1,6),cancellationToken:alreadyCancelled.Token);}
    catch(OperationCanceledException){rejected=true;}
    Need(rejected&&client.State==SessionState.Connected,"pre-cancelled request never starts camera and preserves session");
   }
   Need(await client.SetSaveDestinationAsync(SaveDestination.Computer)==SaveDestination.Computer,"computer-only readback");
   if(isos.Contains(100u))await client.SetIsoAsync(100);
   string lastProgressKey="";var lastProgressTime=DateTimeOffset.MinValue;
   var progress=new InlineProgress<CaptureSnapshot>(s=>{
    var key=$"{s.CaptureId}/{s.State}/{s.Exposure?.Phase}";
    if(s.CaptureId!=0&&(key!=lastProgressKey||DateTimeOffset.UtcNow-lastProgressTime>TimeSpan.FromSeconds(1))) {
     lastProgressKey=key;lastProgressTime=DateTimeOffset.UtcNow;
     Write($"PROGRESS id={s.CaptureId} code={s.Code} state={s.State} phase={s.Exposure?.Phase} target={s.Exposure?.RequestedSeconds:R}s hostHold={s.Exposure?.SoftwareHoldSeconds:R}s cameraElapsed={s.Exposure?.CameraElapsedSeconds}");
    }
   });
   async Task<CaptureSnapshot> Capture(string prefix,ExposureRequest request,CancellationToken token=default) {
    var result=await client.CaptureAsync(receive,prefix,request,progress,token);captures.Add(result);Save("captures.json",captures);
    Write($"RESULT {JsonSerializer.Serialize(result,json)}");
    if(result.IsSuccess) {
     Need(new FileInfo(result.ArwPath).Length==(long)result.FileBytes,"returned exact path has declared bytes");
     using var stream=File.OpenRead(result.ArwPath);
     samples.Add(new {id=result.CaptureId,path=result.ArwPath,bytes=result.FileBytes,sha256=Convert.ToHexString(SHA256.HashData(stream)),requestedSeconds=result.Exposure?.RequestedSeconds,mode=result.Exposure?.Mode});
     Save("samples.json",samples);
    }else Need(result.ArwPath.Length==0&&result.FileBytes==0,"failure exposes no file");
    return result;
   }
   async Task Recover(CaptureSnapshot failed) {
    Need(client.State==SessionState.Unconfirmed,"failed task quarantines session");
    var blocked=false;
    try {var refused=await client.CaptureAsync(receive,"BLOCKED",new(ExposureMode.Bulb,1,6));blocked=!refused.IsSuccess&&refused.CaptureId==0;}
    catch(CameraException e){blocked=e.Error.Code==ProjectCode.State;}
    catch(InvalidOperationException){blocked=true;}
    Need(blocked,"next capture rejected before explicit recovery");
    var recovery=await client.ReconcileAsync(TimeSpan.FromSeconds(30));Save("recovery.json",recovery);
    Need(recovery.Recovered,"explicit recovery obtains native proof");
    var archived=await client.GetCaptureAsync(failed.CaptureId);
    Need(!archived.IsSuccess&&archived.State==failed.State&&archived.ArwPath.Length==0,"recovery preserves original failed task");
    await client.CheckCaptureReadyAsync();
   }
   var scenario=Environment.GetEnvironmentVariable("SW_FAKE_CASE")??"";
   if(mode=="fault") {
    Need(fake,"fault injection only runs against isolated fake");
    CaptureSnapshot failed;
    if(scenario=="bulb-early") {
     var task=Capture("EARLY",new(ExposureMode.Bulb,5,10));
     await Task.Delay(300);await client.EndExposureAsync();failed=await task;
     Need(failed.Code==ProjectCode.EndedEarly,"early end is distinct from success/cancel");
    }else if(scenario=="bulb-exposure-timeout")failed=await Capture("LIMIT",new(ExposureMode.Bulb,10,0.5));
    else if(scenario=="bulb-download-timeout")failed=await Capture("LIMIT",new(ExposureMode.Bulb,1,6,0.05));
    else failed=await Capture("FAULT",new(ExposureMode.Bulb,1,6));
    Need(!failed.IsSuccess&&failed.CaptureId!=0,"injected fault preserved");
    if(scenario is "bulb-upfail" or "bulb-upfail-both")Need(failed.Exposure?.ReleaseError.SonyCode==0x8402&&failed.Exposure.ReleaseAttempts==2,"release secondary code and bounded attempts preserved");
    if(scenario=="bulb-restorefail")Need(failed.Exposure?.RestoreError.SonyCode==0x8402&&failed.Exposure.RestoreState==3,"restoration secondary error preserved");
    if(scenario is "bulb-upfail-both" or "bulb-restorefail") {
     var recovery=await client.ReconcileAsync(TimeSpan.FromSeconds(2));Save("recovery.json",recovery);Need(!recovery.Recovered,"irrecoverable fault remains quarantined");
     restoreNeeded=false; // no setter into unconfirmed camera; original values remain in baseline record
     expectedUnconfirmedDispose=true;
    }else await Recover(failed);
   }else {
    for(var i=0;i<2;i++)Need((await Capture("CSORD",new(ExposureMode.Ordinary,baseline.ExposureSeconds,5))).IsSuccess,"sequential ordinary capture through C#");
    var bulb=await Capture("CSBULB",new(ExposureMode.Bulb,2,7));Need(bulb.IsSuccess,"2-second Bulb through C#");
    Need(bulb.ActualParameters?.ShutterKind==ShutterKind.Bulb&&double.IsNaN(bulb.ActualParameters.ExposureSeconds),"Bulb actual sensor duration is not fabricated");
    Need(bulb.Exposure is {RestoreState:2}&&double.IsFinite(bulb.Exposure.SoftwareHoldSeconds),"Bulb temporary settings restored and software timing reported");
    await client.DisconnectAsync();await client.ConnectAsync(cameras[0].Index);
    await client.SetSaveDestinationAsync(SaveDestination.Computer);
    Need((await Capture("CSRECON",new(ExposureMode.Ordinary,baseline.ExposureSeconds,5))).IsSuccess,"normal reconnect capture in same process");
    using var cancel=new CancellationTokenSource();cancel.CancelAfter(1100);
    var cancelled=await Capture("CSCANCEL",new(ExposureMode.Bulb,10,15),cancel.Token);
    Need(cancelled.Code==ProjectCode.Cancelled&&cancelled.State==CaptureState.CancelledUnconfirmed,"CancellationToken submits real native cancellation and retains result");
    await Recover(cancelled);
    Need(captures.Select(x=>x.CaptureId).Distinct().Count()==captures.Count,"all accepted captures have unique IDs");
    Need(captures.Where(x=>x.IsSuccess).Select(x=>x.ArwPath).Distinct().Count()==4,"four distinct success paths, no last-file fallback");
   }
  }
  success=true;
 }
}catch(Exception e){Write($"FAIL {e}");}
finally {
 try {
  if(restoreNeeded&&baseline!=null&&originalSave!=null) {
   if(client.State==SessionState.Unconfirmed) {
    var recovery=await client.ReconcileAsync(TimeSpan.FromSeconds(30));Save("cleanup-recovery.json",recovery);
   }
   if(client.State!=SessionState.Connected)throw new InvalidOperationException("RESTORE_UNCONFIRMED: 禁止对未确认会话强行写参数");
   Need(await client.SetExposureSecondsAsync(baseline.ExposureSeconds)==baseline.ExposureSeconds,"final shutter restoration readback");
   Need(await client.SetIsoAsync(baseline.Iso)==baseline.Iso,"final ISO restoration readback");
   Need(await client.SetSaveDestinationAsync(originalSave.Value)==originalSave.Value,"final save destination restoration readback");
   var final=await client.ReadParametersAsync();var finalSave=await client.GetSaveDestinationAsync();Save("final-state.json",new{parameters=final,saveDestination=finalSave});
   Need(final==baseline&&finalSave==originalSave,"all test-modified settings match original baseline");await client.CheckCaptureReadyAsync();
  }
 }catch(Exception e){success=false;Write($"CLEANUP {e}");}
 try{await client.DisposeAsync();if(expectedUnconfirmedDispose){success=false;Write("FAIL expected Dispose to report unconfirmed state");}}
 catch(Exception e){if(expectedUnconfirmedDispose&&client.State==SessionState.Unconfirmed)Write($"CHECK PASS Dispose retains and reports injected unconfirmed camera state: {e.Message}");else{success=false;Write($"DISPOSE {e}");}}
 try{Need(threadIds.Count==1,"all logged native calls executed on one owner thread");}catch{success=false;}
 var binaryHashes=new Dictionary<string,string>();
 foreach(var binary in new[]{typeof(SonyCameraClient).Assembly.Location,typeof(Program).Assembly.Location}) {using var stream=File.OpenRead(binary);binaryHashes[Path.GetFileName(binary)]=Convert.ToHexString(SHA256.HashData(stream));}
 Save("result.json",new {success,mode,backend=fake?"FAKE":"REAL",native,run,threadIds,client.State,wrapperSha256=client.Abi?.WrapperSha256,binaryHashes});
 Write($"ACCEPTANCE {(success?"PASS":"FAIL")} backend={(fake?"FAKE":"REAL")} logs={run}");
}
return success?0:1;

sealed class InlineProgress<T>(Action<T> action):IProgress<T> { public void Report(T value)=>action(value); }
