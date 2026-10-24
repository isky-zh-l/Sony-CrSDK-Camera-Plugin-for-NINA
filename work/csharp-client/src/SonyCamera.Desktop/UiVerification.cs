using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using SonyCamera.Client;

namespace SonyCamera.Desktop;

internal sealed partial class MainForm {
    readonly Dictionary<Button,int> eventCounts=new();
    readonly Dictionary<ulong,CaptureSnapshot> terminalCaptures=new();
    readonly List<object> verificationSteps=new();
    static readonly JsonSerializerOptions EvidenceJson=new(){WriteIndented=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals,Converters={new JsonStringEnumConverter()}};
    string? verificationDirectory;
    int verificationStep;
    string? verificationModel;
    DeploymentEvidence? deploymentEvidence;
    OperatorErrorEvidence? lastOperatorError;
    sealed record OperatorErrorEvidence(string ExceptionType,string Message,int HResult,CameraError? CameraError);
    sealed record DeploymentEvidence(string? Backend,string EvidenceClassification,string? ManifestPath,string? ApprovedManifestSha256,string? ActualManifestSha256,bool? ApprovedManifestMatches,string? PackageId,string? ReadError);

    DeploymentEvidence ReadDeploymentEvidence() {
        string? manifest=null, approved=null;
        try {
            var configuration=CameraClientConfiguration.Load(options.ConfigPath);manifest=configuration.ManifestPath;approved=configuration.ApprovedManifestSha256;
            if(new FileInfo(manifest).Length>1024*1024)throw new InvalidDataException("清单超过 1 MiB，拒绝为留证读取。");
            byte[] bytes=File.ReadAllBytes(manifest);using var json=JsonDocument.Parse(bytes);
            string? backend=json.RootElement.TryGetProperty("backend",out var value)?value.GetString():null;
            string? package=json.RootElement.TryGetProperty("packageId",out var id)?id.GetString():null;
            string actual=Convert.ToHexString(SHA256.HashData(bytes));
            string classification=options.TestOnly||backend=="test"?"SIMULATED: test-only mode or test backend; not hardware evidence":"Production controls verification; hardware results require successful initialized production package";
            return new(backend,classification,manifest,approved,actual,string.Equals(approved,actual,StringComparison.OrdinalIgnoreCase),package,null);
        }catch(Exception ex){return new(null,options.TestOnly?"SIMULATED: explicit test-only mode; configuration unavailable":"Deployment not yet verified",manifest,approved,null,null,null,ex.ToString());}
    }

    void RegisterVerificationEvents() {
        foreach(var pair in new[]{(enumerate,"enumerate"),(connect,"connect"),(disconnect,"disconnect"),(refresh,"refresh"),(apply,"apply"),(single,"single"),(sequence,"sequence"),(cancel,"cancel"),(end,"end"),(recover,"recover"),(open,"open")}) {
            pair.Item1.Name=pair.Item2;eventCounts[pair.Item1]=0;
            pair.Item1.Click+=(_,_)=>eventCounts[pair.Item1]++;
        }
    }
    void RecordTerminalCapture(CaptureSnapshot snapshot){if(!snapshot.IsPending&&snapshot.CaptureId>0)terminalCaptures[snapshot.CaptureId]=snapshot;}
    string SaveScreenshot(string name) {
        string path=Path.Combine(verificationDirectory??options.LogDirectory,$"{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        using var bitmap=new Bitmap(Width,Height);DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);return path;
    }
    void SaveJson(string path,object value)=>File.WriteAllText(path,JsonSerializer.Serialize(value,EvidenceJson),System.Text.Encoding.UTF8);
    void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    async Task WaitUntil(Func<bool> predicate,string description,int timeoutSeconds=180) {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        while(!predicate()){if(watch.Elapsed.TotalSeconds>timeoutSeconds)throw new TimeoutException($"界面验收等待超时：{description}");await Task.Delay(50);}
        await Task.Yield();
    }
    void PerformRealClick(Button button) {
        Require(button.Enabled,$"按钮 {button.Name} 应允许操作，但当前已禁用。");
        int before=eventCounts[button];button.PerformClick();
        Require(eventCounts[button]==before+1,$"按钮 {button.Name} 的 Click 事件没有执行一次。");
        WriteLog($"程序驱动真实控件：{button.Name}.PerformClick()；事件计数 {eventCounts[button]}");
    }
    async Task ClickAndWait(Button button,bool allowError=false) {
        int before=errorCount;PerformRealClick(button);
        await WaitUntil(()=>!busy&&!shooting&&controlOperations==0,button.Name+" 完成");
        if(!allowError)Require(errorCount==before,$"按钮 {button.Name} 处理失败：{lastError}");
    }
    void AssertDisabled(params Button[] buttons) {
        foreach(var button in buttons){Require(!button.Enabled,$"{button.Name} 在当前状态本应禁用。");int before=eventCounts[button];button.PerformClick();Require(eventCounts[button]==before,$"禁用按钮 {button.Name} 意外执行了事件。");}
    }
    async Task VerifyStep(string name,Func<Task> action) {
        int number=++verificationStep;Exception? failure=null;
        try{await action();}catch(Exception ex){failure=ex;throw;}
        finally {
            string label=$"{number:D2}-{name}";
            string? screenshot=null;try{screenshot=SaveScreenshot(label);}catch(Exception ex){WriteLog("截图失败："+ex);if(failure==null)throw;}
            var record=new{Step=number,Name=name,Passed=failure==null,Time=DateTimeOffset.Now,Automation="Program drives actual WinForms controls through PerformClick; not a manual click test",Backend=deploymentEvidence?.Backend,TestOnly=options.TestOnly,ConfigPath=options.ConfigPath,ApprovedManifest=deploymentEvidence,SelectedModel=verificationModel,State=client?.State??SessionState.New,Error=failure?.ToString(),DisplayedError=lastError,OperatorError=lastOperatorError,ErrorCount=errorCount,Progress=progress.Text,Parameters=readParameters,Destination=readDestination,Latest=latest,Captures=terminalCaptures.Values.ToArray(),Controls=eventCounts.ToDictionary(x=>x.Key.Name,x=>new{Enabled=x.Key.Enabled,ClickEvents=x.Value}),Screenshot=screenshot,LogPath=logPath};
            verificationSteps.Add(record);SaveJson(Path.Combine(verificationDirectory!,label+".json"),record);WriteLog($"界面验收步骤 {number} {name}：{(failure==null?"通过":"失败")}");
        }
    }

    async Task RunUiVerification() {
        verificationDirectory=Path.Combine(options.LogDirectory,$"verify-ui-{DateTime.Now:yyyyMMdd-HHmmss-fff}");Directory.CreateDirectory(verificationDirectory);
        deploymentEvidence=ReadDeploymentEvidence();WriteLog($"验收运行包：backend={deploymentEvidence.Backend}；testOnly={options.TestOnly}；{deploymentEvidence.EvidenceClassification}；清单={deploymentEvidence.ManifestPath}；外部批准={deploymentEvidence.ApprovedManifestSha256}");
        WriteLog("开始程序驱动真实 WinForms 控件的验收；这是自动控件操作，不是人工点击。相机必须由本进程独占。");
        CameraParameters? baseline=null;SaveDestination baselineDestination=SaveDestination.Computer;
        bool changed=false,restored=false,passed=false;string? failure=null;
        try {
            await VerifyStep("initial-disabled",()=>{AssertDisabled(connect,disconnect,refresh,apply,single,sequence,cancel,end,recover);return Task.CompletedTask;});
            await VerifyStep("enumerate",async()=>{await ClickAndWait(enumerate);Require(cameras.Items.Count>0,"未枚举到相机；停止验收，不触发拍摄。");cameras.SelectedIndex=0;var camera=((CameraChoice)cameras.SelectedItem!).Camera;verificationModel=options.TestOnly?$"SIMULATED: {camera.Model}":camera.Model;if(!options.TestOnly)Require(camera.Model.Contains("ILCE-7M5",StringComparison.OrdinalIgnoreCase),"正式验收只允许 Sony A7 V（ILCE-7M5）；当前型号不符，停止且不连接。");WriteLog($"验收所选型号：{verificationModel}");});
            await VerifyStep("connect-baseline",async()=>{
                await ClickAndWait(connect);Require(client?.State==SessionState.Connected,"连接后未进入已连接状态。");await ClickAndWait(refresh);
                baseline=readParameters??throw new InvalidOperationException("连接没有读回参数。");baselineDestination=readDestination;
                Require(baseline.ShutterKind==ShutterKind.Seconds&&double.IsFinite(baseline.ExposureSeconds),"为确保通过真实控件恢复，验收前请将相机设为普通快门。");
                Require(iso.Items.Contains(baseline.Iso)&&shutters.Items.Contains(baseline.ExposureSeconds),"基线 ISO 或快门不在真实候选内；无法保证原样恢复，停止且不修改参数。");
                SaveJson(Path.Combine(verificationDirectory!,"baseline.json"),new{Parameters=baseline,Destination=baselineDestination});AssertDisabled(connect,enumerate,cancel,end,recover);
            });
            await VerifyStep("select-and-apply",async()=>{
                mode.SelectedIndex=0;
                iso.SelectedItem=iso.Items.Cast<uint>().FirstOrDefault(x=>x!=baseline!.Iso,baseline!.Iso);
                shutters.SelectedItem=shutters.Items.Cast<double>().Where(x=>x>0&&x<=1).OrderBy(x=>Math.Abs(x-0.1)).First();
                destination.SelectedIndex=0;
                uint selectedIso=(uint)iso.SelectedItem!;double selectedSeconds=(double)shutters.SelectedItem!;changed=true;
                await ClickAndWait(apply);Require(readParameters!.Iso==selectedIso&&readParameters.ExposureSeconds==selectedSeconds&&readDestination==SaveDestination.Computer,"应用后的真实参数读回与所选候选不一致。");
            });
            await VerifyStep("invalid-directory-error-no-shutter",async()=>{
                string valid=folder.Text;string blocker=Path.Combine(options.SettingsDirectory,"verify-not-a-directory.txt");File.WriteAllText(blocker,"This file intentionally blocks directory creation; retained as UI error evidence.");
                int oldErrors=errorCount,oldCaptures=terminalCaptures.Count;ulong oldId=latest?.CaptureId??0;
                try{folder.Text=Path.Combine(blocker,"child");await ClickAndWait(single,allowError:true);Require(errorCount==oldErrors+1&&progress.Text.StartsWith("操作失败："),"非法接收目录未通过相同 ShowError 路径显示错误。");Require(terminalCaptures.Count==oldCaptures&&(latest?.CaptureId??0)==oldId,"非法目录检查意外产生了拍摄结果。");}finally{folder.Text=valid;}
            });
            await VerifyStep("ordinary-two-sequence",async()=>{
                int before=terminalCaptures.Count;count.Value=2;mode.SelectedIndex=0;await ClickAndWait(sequence);
                var added=terminalCaptures.Values.Skip(before).ToArray();Require(added.Length==2&&added.All(x=>x.IsSuccess&&x.Exposure?.Mode==ExposureMode.Ordinary),"普通顺序任务未得到恰好两张成功结果；失败不补拍。");
                Require(added[0].CaptureId<added[1].CaptureId,"拍摄编号未递增。");Require(open.Enabled,"成功结果应允许打开文件夹。");
            });
            await VerifyStep("bulb-two-seconds",async()=>{
                int before=terminalCaptures.Count;mode.SelectedIndex=1;seconds.Value=2;await ClickAndWait(single);
                var added=terminalCaptures.Values.Skip(before).ToArray();Require(added.Length==1&&added[0].IsSuccess&&added[0].Exposure?.Mode==ExposureMode.Bulb&&added[0].Exposure?.RequestedSeconds==2,"2 秒 B 门未得到成功结果；失败不补拍。");
            });
            await VerifyStep("bulb-cancel-holding",async()=>{
                ulong previous=latest!.CaptureId;mode.SelectedIndex=1;seconds.Value=10;PerformRealClick(single);
                await WaitUntil(()=>latest?.CaptureId>previous&&latest.Exposure?.Phase==ExposurePhase.Holding&&cancel.Enabled,"10 秒 B 门保持阶段",30);
                AssertDisabled(connect,disconnect,enumerate,refresh,apply,single,sequence,recover);Require(end.Enabled,"B 门保持阶段应允许提前结束按钮。");SaveScreenshot("cancel-before-click-holding");
                await ClickAndWait(cancel);var cancelled=latest??throw new InvalidOperationException("取消后没有拍摄结果。");Require(!cancelled.IsSuccess&&!cancelled.IsPending&&string.IsNullOrEmpty(cancelled.ArwPath),"取消后不得返回成功或文件路径。");Require(cancelled.Code==ProjectCode.Cancelled||cancelled.State==CaptureState.CancelledUnconfirmed,"取消结果未保留取消语义。");
            });
            await VerifyStep("explicit-recovery",async()=>{
                Require(client?.State==SessionState.Unconfirmed,"取消后预期需要显式恢复，实际状态不符；不擅自继续。");await ClickAndWait(recover);Require(client?.State==SessionState.Connected,"显式恢复未确认相机可继续操作；停止。");
                AssertDisabled(cancel,end,recover);
            });
            passed=true;
        }catch(Exception ex){failure=ex.ToString();WriteLog("界面自动验收失败："+ex);}
        finally {
            if(changed&&baseline!=null) {
                try {
                    await VerifyStep("restore-baseline-through-controls",async()=>{
                        await WaitUntil(()=>!busy&&!shooting&&controlOperations==0,"恢复前等待操作收尾");
                        Require(client?.State==SessionState.Connected,"未确认已连接，不能继续写参数；保留隔离，请人工检查相机。");
                        mode.SelectedIndex=0;iso.SelectedItem=baseline.Iso;shutters.SelectedItem=baseline.ExposureSeconds;destination.SelectedIndex=baselineDestination==SaveDestination.Computer?0:baselineDestination==SaveDestination.ComputerAndCard?1:2;
                        Require(iso.SelectedItem is uint&&shutters.SelectedItem is double,"基线候选无法选择，停止恢复。");await ClickAndWait(apply);
                        Require(readParameters?.Iso==baseline.Iso&&readParameters.IsoKind==baseline.IsoKind&&readParameters.ShutterKind==baseline.ShutterKind&&readParameters.ExposureSeconds==baseline.ExposureSeconds&&readDestination==baselineDestination,"收尾参数读回与基线不一致，不能宣称恢复成功。");restored=true;
                    });
                }catch(Exception ex){passed=false;failure=(failure??"")+"\n恢复失败："+ex;WriteLog("收尾恢复失败，停止后续控件操作："+ex);}
            }else restored=true;
            if(restored&&client?.State==SessionState.Connected) {
                try{await VerifyStep("disconnect",async()=>{await ClickAndWait(disconnect);Require(client.State==SessionState.Disconnected,"正常断开未进入已断开状态。");AssertDisabled(single,sequence,refresh,apply,cancel,end,recover);});}
                catch(Exception ex){passed=false;failure=(failure??"")+"\n断开失败："+ex;}
            }
            Environment.ExitCode=passed&&restored?0:1;
            SaveJson(Path.Combine(verificationDirectory!,"summary.json"),new{Passed=Environment.ExitCode==0,ExitCode=Environment.ExitCode,Automation="Actual WinForms controls, program driven; not manual clicking",Backend=deploymentEvidence?.Backend,TestOnly=options.TestOnly,ConfigPath=options.ConfigPath,ApprovedManifest=deploymentEvidence,SelectedModel=verificationModel,OperatorError=lastOperatorError,Baseline=baseline,BaselineDestination=baselineDestination,BaselineRestored=restored,Captures=terminalCaptures.Values.ToArray(),Failure=failure,Steps=verificationSteps,LogPath=logPath});
            WriteLog($"界面自动验收结束：退出码 {Environment.ExitCode}；证据目录 {verificationDirectory}");Close();
        }
    }
}
