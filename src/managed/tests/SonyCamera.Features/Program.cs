using SonyCamera.Client;
using System.Text.Json;
if(args.Contains("-j")){
 Console.WriteLine(args.Last().Contains("missing")?"[{}]":args.Last().Contains("texttag")?"[{\"CameraTemperature\":\"unknown\"}]":"[{\"CameraTemperature\":23}]");return 0;
}
void Need(bool pass,string message){Console.WriteLine($"CHECK {(pass?"PASS":"FAIL")} {message}");if(!pass)throw new InvalidOperationException(message);}
var config=CameraClientConfiguration.Load(Path.GetFullPath(args[0]));var scenario=Environment.GetEnvironmentVariable("SW_FAKE_CASE")??"";
Need(config.AllowTestPackage,"requires isolated test backend");
var root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
await using var client=new SonyCameraClient(config.ToDeploymentOptions());
await client.InitializeAsync();await client.CheckFeatureAbiAsync();
async Task Connect(CameraConnectionMode mode){var cameras=await client.EnumerateAsync();Need(cameras.Count==1&&cameras[0].Model=="SIMULATED","refuse real camera");await client.ConnectAsync(cameras[0].Index,mode);}
await Connect(CameraConnectionMode.Remote);
if(scenario=="feature-normal"||scenario=="feature-unsupported"){
 var properties=await client.GetPropertiesAsync();Need(properties.Count==45,"all curated properties probed");
 if(scenario=="feature-unsupported"){Need(properties.Single(x=>x.Property==CameraProperty.MeteringMode).Info==null,"unsupported probe retained");await client.CheckCaptureReadyAsync();}
 else {
  var aperture=(await client.GetPropertyAsync(CameraProperty.Aperture)).Info!;Need(aperture.Current==280&&aperture.Candidates.SequenceEqual(new long[]{280,400,560}),"exact aperture candidates");
  Need(await client.SetApertureAsync(4.0)==4.0,"physical aperture convenience");
  Need(await client.SetFocusPositionAsync(12000)==12000,"absolute focus readback");await client.FocusPulseAsync(-1,TimeSpan.FromMilliseconds(1));
  await client.StartLiveViewAsync();var frame=await client.GetLiveViewFrameAsync();Need(frame!=null&&frame.Jpeg.Length>0&&frame.Jpeg[0]==255,"owned preview bytes");
  var rejected=false;try{await client.CheckCaptureReadyAsync();}catch(CameraException){rejected=true;}Need(rejected,"still start blocked while preview active");
  await client.StopLiveViewAsync();Need(!client.LiveViewEnabled,"preview stopped");
  rejected=false;try{await client.GetLiveViewFrameAsync();}catch(CameraException){rejected=true;}Need(rejected,"no stale preview after stop");
  await client.StartMovieAsync();Need(client.MovieRecording,"movie state confirmed");await client.StopMovieAsync();Need(!client.MovieRecording,"movie stopped");
  await client.CheckCaptureReadyAsync();
 }
}else if(scenario=="feature-no-lens"){
 var p=await client.ReadParametersAsync();Need(double.IsNaN(p.FNumber),"unknown lens aperture");
 var photo=await client.CaptureAsync(root,"NOLENS",new ExposureRequest(ExposureMode.Ordinary,p.ExposureSeconds,10,10));Need(photo.IsSuccess&&double.IsNaN(photo.ActualParameters!.FNumber),"lensless complete RAW control chain");
}else if(scenario=="feature-focus-stop-fail"){
 bool failed=false;try{await client.FocusPulseAsync(1,TimeSpan.FromMilliseconds(1));}catch(CameraException){failed=true;}Need(failed&&client.State==SessionState.Unconfirmed,"focus failure quarantines managed session");
}else if(scenario=="feature-set-fail"||scenario=="feature-set-timeout"){
 bool failed=false;try{await client.SetPropertyAsync(CameraProperty.FocusMode,2);}catch(CameraException){failed=true;}Need(failed&&client.State==SessionState.Unconfirmed,"setter failure quarantines managed session");
}
await client.DisconnectAsync();await Connect(CameraConnectionMode.Contents);
var folders=await client.GetFoldersAsync();Need(folders.Count==1,"managed folder list");
var contents=await client.GetContentsAsync(folders[0]);Need(contents.Count==1&&contents[0].Bytes==(scenario=="feature-zero-size"?0ul:4096ul),"managed original content metadata");
using var cancel=new CancellationTokenSource();if(scenario=="feature-transfer-pending")cancel.CancelAfter(100);
if(scenario=="feature-zero-size"){
 bool rejected=false;try{await client.DownloadContentAsync(contents[0],root,TimeSpan.FromSeconds(2));}catch(CameraException ex){rejected=ex.Error.Code==ProjectCode.Unsupported;}Need(rejected,"unknown original size rejected");
}else {
 var result=await client.DownloadContentAsync(contents[0],root,scenario=="feature-transfer-timeout"?TimeSpan.FromMilliseconds(1):TimeSpan.FromSeconds(2),cancellationToken:cancel.Token);
 if(scenario is "feature-wrong-size" or "feature-wrong-handle" or "feature-transfer-error" or "feature-transfer-pending" or "feature-transfer-timeout")Need(!result.IsSuccess&&result.Path==""&&client.State==SessionState.Unconfirmed,"failed download never hands out partial path");
 else Need(result.IsSuccess&&result.VerifiedBytes==4096&&new FileInfo(result.Path).Length==4096,"complete original verified through C# and C ABI");
}
await client.DisconnectAsync();
foreach(var kind in new[]{"temperature","missing","texttag"}){
 var source=Path.Combine(root,kind+".arw");await File.WriteAllTextAsync(source,"TEST ONLY");
 var temperature=await ArwMetadata.ReadTemperatureAsync(source,Environment.ProcessPath!);
 Need(kind=="temperature"?temperature.Celsius==23:temperature.Celsius==null,"external metadata result "+kind);
}
Console.WriteLine("MANAGED FEATURE PASS "+scenario);return 0;
