using System.Diagnostics;
using System.Globalization;
using SonyCamera.Client;

namespace SonyCamera.Desktop;

/// <summary>只使用托管调用层；此项目不含任何相机原生互操作。</summary>
internal sealed partial class MainForm : Form {
    SonyCameraClient? client;
    SonyCameraClient Client { get { if(client!=null)return client; var configuration=CameraClientConfiguration.Load(options.ConfigPath); if(configuration.AllowTestPackage&&!options.TestOnly)throw new InvalidOperationException("配置请求了测试包，但桌面未指定 --test-only；已拒绝加载。");client=new SonyCameraClient(configuration.ToDeploymentOptions());client.Log+=OnClientLog;return client;} }
    readonly DesktopOptions options;
    readonly bool smoke;
    readonly string logPath;
    readonly StreamWriter logWriter;
    readonly ComboBox cameras=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=260};
    readonly ComboBox iso=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=140};
    readonly ComboBox shutters=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=180};
    readonly ComboBox mode=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=140};
    readonly ComboBox destination=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=140};
    readonly NumericUpDown seconds=new(){DecimalPlaces=3,Minimum=1,Maximum=900,Value=2,Increment=.001m,Width=130};
    readonly NumericUpDown count=new(){Minimum=1,Maximum=9999,Value=1,Width=90};
    readonly TextBox folder=new(){Width=520};
    readonly Label state=new(){AutoSize=true,Text="尚未初始化"};
    readonly Label current=new(){AutoSize=true,Text="当前参数：—"};
    readonly Label progress=new(){AutoSize=true,Text="等待操作"};
    readonly TextBox logs=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,WordWrap=false};
    readonly Button enumerate=new(){Text="初始化 / 枚举",AutoSize=true};
    readonly Button connect=new(){Text="连接",AutoSize=true};
    readonly Button disconnect=new(){Text="断开",AutoSize=true};
    readonly Button refresh=new(){Text="读取参数",AutoSize=true};
    readonly Button apply=new(){Text="应用参数",AutoSize=true};
    readonly Button single=new(){Text="拍摄单张",AutoSize=true};
    readonly Button sequence=new(){Text="开始顺序拍摄",AutoSize=true};
    readonly Button cancel=new(){Text="取消任务",AutoSize=true};
    readonly Button end=new(){Text="提前结束曝光",AutoSize=true};
    readonly Button recover=new(){Text="显式恢复",AutoSize=true};
    readonly Button features=new(){Text="扩展功能（预览 / 录像 / 更多参数）",AutoSize=true};
    readonly Button open=new(){Text="打开成功文件夹",AutoSize=true};
    bool busy, shooting, closing, allowClose, initialized;
    volatile bool stopSequence;
    CaptureSnapshot? latest;
    Task? activeTask;
    Task? activeOperation;
    int controlOperations;
    CameraParameters? readParameters;
    SaveDestination readDestination;
    int errorCount;
    string? lastError;
    DateTime lastCaptureLog=DateTime.MinValue;
    string lastCaptureLogKey="";

    public MainForm(DesktopOptions options) {
        this.options=options;
        smoke=options.Smoke;
        Directory.CreateDirectory(options.LogDirectory);
        Directory.CreateDirectory(options.SettingsDirectory);
        folder.Text=options.CaptureDirectory;
        logPath=Path.Combine(options.LogDirectory,$"desktop-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
        logWriter=new StreamWriter(logPath,append:false,System.Text.Encoding.UTF8){AutoFlush=true};
        Text="Sony A7 V · C# 桌面测试"; Width=1120; Height=760; MinimumSize=new Size(850,650);
        Font=new Font("Microsoft YaHei UI",9F); AutoScaleMode=AutoScaleMode.Dpi;
        mode.Items.AddRange(new object[]{"普通快门","B 门"}); mode.SelectedIndex=0;
        destination.Items.AddRange(new object[]{"仅电脑","电脑 + 相机卡","仅相机卡（用于恢复原设置）"}); destination.SelectedIndex=0;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=8,Padding=new Padding(12)};
        for(int i=0;i<7;i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.Controls.Add(Row(enumerate,cameras,connect,disconnect,state));
        features.Click+=async(_,_)=>await RunOperation(async()=>{if(!initialized)throw new InvalidOperationException("请先枚举并连接相机");if(Client.State!=SessionState.Connected)throw new InvalidOperationException("请先连接并结束当前拍摄");using var dialog=new FeatureForm(Client,folder.Text);dialog.ShowDialog(this);if(Client.State==SessionState.Connected&&Client.ConnectionMode==CameraConnectionMode.Remote)await ReadParameters();});
        layout.Controls.Add(Row(current,refresh,features));
        layout.Controls.Add(Row(new Label{Text="ISO",AutoSize=true},iso,new Label{Text="普通快门（秒）",AutoSize=true},shutters,apply));
        layout.Controls.Add(Row(new Label{Text="曝光模式",AutoSize=true},mode,new Label{Text="B 门秒数",AutoSize=true},seconds,new Label{Text="保存位置",AutoSize=true},destination));
        var browse=new Button{Text="浏览…",AutoSize=true};
        layout.Controls.Add(Row(new Label{Text="接收目录",AutoSize=true},folder,browse));
        layout.Controls.Add(Row(single,new Label{Text="张数",AutoSize=true},count,sequence,cancel,end,recover,open));
        layout.Controls.Add(Row(progress)); layout.Controls.Add(logs); Controls.Add(layout);
        enumerate.Click+=async(_,_)=>await RunOperation(Enumerate);
        connect.Click+=async(_,_)=>await RunOperation(async()=>{if(cameras.SelectedItem is CameraChoice c){await Client.ConnectAsync(c.Camera.Index);await ReadParameters();}});
        disconnect.Click+=async(_,_)=>await RunOperation(async()=>{if(Client.State==SessionState.Unconfirmed)WriteLog("正在尝试断开：参数或连接故障可在成功断开后重连；拍摄状态未确认时，断开不会解除拍摄隔离，仍需按规定恢复。");await Client.DisconnectAsync();});
        refresh.Click+=async(_,_)=>await RunOperation(ReadParameters);
        apply.Click+=async(_,_)=>await RunOperation(ApplyParameters);
        mode.SelectedIndexChanged+=(_,_)=>UpdateControls();
        browse.Click+=(_,_)=>{using var dialog=new FolderBrowserDialog{InitialDirectory=folder.Text};if(dialog.ShowDialog(this)==DialogResult.OK)folder.Text=dialog.SelectedPath;};
        single.Click+=async(_,_)=>await StartSequence(1);
        sequence.Click+=async(_,_)=>await StartSequence((int)count.Value);
        cancel.Click+=async(_,_)=>{stopSequence=true;await RunOperation(async()=>ShowCapture(await Client.CancelAsync()),duringCapture:true);};
        end.Click+=async(_,_)=>{stopSequence=true;await RunOperation(async()=>ShowCapture(await Client.EndExposureAsync()),duringCapture:true);};
        recover.Click+=async(_,_)=>await RunOperation(async()=>{var r=await Client.ReconcileAsync(TimeSpan.FromSeconds(15));ShowCapture(r.Capture);WriteLog($"恢复：{(r.Recovered?"状态已确认":"仍未确认")}；{r.Error}");});
        open.Click+=(_,_)=>{if(latest?.IsSuccess==true)Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{latest.ArwPath}\""){UseShellExecute=true});};
        RegisterVerificationEvents();
        Shown+=async(_,_)=>{
            WriteLog($"配置：{options.ConfigPath}");WriteLog($"日志：{logPath}");
            WriteLog("请退出其他相机控制程序。普通快门使用真实候选；B 门为软件保持请求，不代表传感器毫秒精度。下载不显示推算百分比。");
            if(smoke){await Task.Delay(1200);try{WriteLog($"桌面启动自检：窗体已显示；尺寸 {Width} × {Height}；关键按钮数 {CountButtons(this)}；SDK 未初始化，未调用相机。");WriteLog("桌面启动自检截图："+SaveScreenshot("desktop-smoke"));}catch(Exception ex){Environment.ExitCode=1;WriteLog(ex.ToString());}Close();}
            else if(options.Verify)await RunUiVerification();
            else if(!File.Exists(options.ConfigPath)){progress.Text="尚未配置原生运行包。请提供用户配置文件，再点击初始化。";WriteLog($"配置文件不存在：{options.ConfigPath}；请按运行说明设置 ManifestPath 和 ApprovedManifestSha256。");}
        };
        FormClosing+=OnClosing;
        UpdateControls();
    }

    static FlowLayoutPanel Row(params Control[] controls){var row=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Padding=new Padding(0,3,0,3)};foreach(var c in controls){c.Margin=new Padding(4,4,8,4);row.Controls.Add(c);}return row;}
    async Task Enumerate(){if(!initialized){await Client.InitializeAsync();initialized=true;WriteLog($"ABI {Client.Abi?.Version}；包装层 SHA-256：{Client.Abi?.WrapperSha256}");}var list=await Client.EnumerateAsync();cameras.Items.Clear();foreach(var c in list)cameras.Items.Add(new CameraChoice(c));if(cameras.Items.Count>0)cameras.SelectedIndex=0;WriteLog($"枚举到 {list.Count} 台相机。");}
    async Task ReadParameters(){var p=await Client.ReadParametersAsync();readParameters=p;var values=await Client.GetSupportedIsoAsync();iso.Items.Clear();foreach(var value in values)iso.Items.Add(value);iso.SelectedItem=p.Iso;var times=await Client.GetSupportedExposureSecondsAsync();shutters.Items.Clear();foreach(var time in times)shutters.Items.Add(time);shutters.SelectedItem=p.ExposureSeconds;var save=await Client.GetSaveDestinationAsync();readDestination=save;destination.SelectedIndex=save==SaveDestination.ComputerAndCard?1:save==SaveDestination.Computer?0:2;var caps=await Client.GetExposureCapabilitiesAsync();current.Text=$"当前：ISO {p.Iso} ({p.IsoKind})；快门 {p.ShutterKind} / {p.ExposureSeconds:G9} 秒；光圈 f/{p.FNumber:G}";WriteLog($"读取参数：{p}；B 门能力：{caps}");}
    async Task ApplyParameters(){if(iso.SelectedItem is uint value){var actual=await Client.SetIsoAsync(value);WriteLog($"ISO 请求 {value}，读回 {actual}");}if(mode.SelectedIndex==0&&shutters.SelectedItem is double time){var actual=await Client.SetExposureSecondsAsync(time);WriteLog($"普通快门请求 {time:G17} 秒，读回 {actual:G17} 秒");}var result=await Client.SetSaveDestinationAsync(destination.SelectedIndex==0?SaveDestination.Computer:destination.SelectedIndex==1?SaveDestination.ComputerAndCard:SaveDestination.Card);WriteLog($"保存位置读回：{result}");await ReadParameters();}
    async Task StartSequence(int number){if(shooting||busy)return;stopSequence=false;shooting=true;UpdateControls();activeTask=ExecuteSequence(number);try{await activeTask;}finally{shooting=false;activeTask=null;UpdateControls();}}
    async Task ExecuteSequence(int number){try{await ApplyParameters();await Client.CheckCaptureReadyAsync();double requested=mode.SelectedIndex==1?(double)seconds.Value:shutters.SelectedItem is double t?t:throw new InvalidOperationException("请先读取并选择普通快门候选。");var request=new ExposureRequest(mode.SelectedIndex==1?ExposureMode.Bulb:ExposureMode.Ordinary,requested,Math.Ceiling(requested)+30,120);string root=Path.GetFullPath(folder.Text);Directory.CreateDirectory(root);for(int i=0;i<number&&!stopSequence;i++){WriteLog($"顺序任务 {i+1}/{number}；请求 {request.Mode} {requested:G17} 秒");var capture=await Client.CaptureAsync(root,"DESKTOP",request,new Progress<CaptureSnapshot>(ShowCapture));RecordTerminalCapture(capture);ShowCapture(capture);if(!capture.IsSuccess){WriteLog("本张未成功，停止后续拍摄。失败文件保留，不从目录寻找替代文件。");break;}}}catch(Exception e){ShowError(e);}finally{UpdateControls();}}
    async Task RunOperation(Func<Task> action,bool duringCapture=false){if((busy||shooting)&&!duringCapture)return;if(!duringCapture)busy=true;else controlOperations++;UpdateControls();try{var task=action();if(!duringCapture)activeOperation=task;await task;}catch(Exception e){ShowError(e);}finally{if(!duringCapture){busy=false;activeOperation=null;}else controlOperations--;UpdateControls();}}
    void ShowCapture(CaptureSnapshot c){if(latest is{} previous&&(c.CaptureId<previous.CaptureId||(c.CaptureId==previous.CaptureId&&!previous.IsPending&&c.IsPending)))return;RecordTerminalCapture(c);latest=c;string phase=c.Exposure?.Phase switch{ExposurePhase.Idle=>"准备",ExposurePhase.Holding=>"曝光中",ExposurePhase.Downloading=>"等待 / 接收文件",ExposurePhase.Restoring=>"恢复相机设置",ExposurePhase.Terminal=>"本次已结束",_=>"未知"};string captureState=c.State switch{CaptureState.None=>"尚未拍摄",CaptureState.Waiting=>"处理中",CaptureState.Succeeded=>"成功",CaptureState.Failed=>"失败",CaptureState.CancelledUnconfirmed=>"取消 / 状态未确认",CaptureState.TimedOutUnconfirmed=>"超时 / 状态未确认",CaptureState.EndedEarlyUnconfirmed=>"提前结束 / 状态未确认",_=>c.State.ToString()};progress.Text=$"拍摄 #{c.CaptureId}：{captureState}；{phase}；下载通知 {c.DownloadNotifications}；文件 {c.FileBytes:N0} 字节";string key=$"{c.CaptureId}/{c.State}/{c.Code}/{phase}";if(key!=lastCaptureLogKey||DateTime.UtcNow-lastCaptureLog>=TimeSpan.FromSeconds(1)){lastCaptureLogKey=key;lastCaptureLog=DateTime.UtcNow;WriteLog($"拍摄状态：{progress.Text}；项目码 {c.Code}");if(c.Exposure is{} e)WriteLog($"请求 {e.RequestedSeconds:G17} 秒；软件保持 {e.SoftwareHoldSeconds:G17} 秒；实际参数 {c.ActualParameters}；释放错误 {e.ReleaseError}；恢复错误 {e.RestoreError}");if(c.IsSuccess)WriteLog($"成功 ARW：{c.ArwPath}（此处表示下载完成且文件可读；RAW 解码另行验证）");else if(!c.IsPending)WriteLog($"失败详情：{c.Failure}；接收目录：{c.ReceiveDirectory}");}UpdateControls();}
    void ShowError(Exception e){errorCount++;lastError=e.ToString();lastOperatorError=new(e.GetType().FullName??e.GetType().Name,e.Message,e.HResult,(e as CameraException)?.Error);WriteLog(e.ToString());progress.Text="操作失败："+e.Message;if(!closing&&!options.Verify)MessageBox.Show(this,e.Message,"操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    void OnClientLog(ClientLog item){
        if(IsDisposed||Disposing||allowClose||!IsHandleCreated)return;
        if(InvokeRequired){try{BeginInvoke((Action)(()=>OnClientLog(item)));}catch(InvalidOperationException){}return;}
        if(!allowClose)WriteLog($"[所有者线程 {item.OwnerThreadId}] {item.Operation}：{item.Message}");
    }
    void WriteLog(string text){string line=$"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}";logWriter.WriteLine(line);if(logs.TextLength>500000)logs.Text=logs.Text[^250000..];logs.AppendText(line+Environment.NewLine);}
    void UpdateControls(){var s=client?.State??SessionState.New;state.Text=$"状态：{StateLabel(s)}";bool free=!busy&&!shooting&&!closing;bool connected=s==SessionState.Connected&&client?.ConnectionMode==CameraConnectionMode.Remote;features.Enabled=free&&s==SessionState.Connected;enumerate.Enabled=free&&(s==SessionState.New||s==SessionState.Initialized||s==SessionState.Disconnected);cameras.Enabled=enumerate.Enabled;connect.Enabled=free&&cameras.SelectedItem!=null&&(s==SessionState.Initialized||s==SessionState.Disconnected);disconnect.Enabled=free&&(s==SessionState.Connected||s==SessionState.Unconfirmed);refresh.Enabled=apply.Enabled=free&&connected;iso.Enabled=mode.Enabled=destination.Enabled=folder.Enabled=count.Enabled=free&&connected;shutters.Enabled=free&&connected&&mode.SelectedIndex==0;seconds.Enabled=free&&connected&&mode.SelectedIndex==1;single.Enabled=sequence.Enabled=free&&connected;cancel.Enabled=shooting&&!closing&&controlOperations==0&&s==SessionState.Capturing;end.Enabled=shooting&&!closing&&controlOperations==0&&s==SessionState.Capturing&&latest?.Exposure?.Mode==ExposureMode.Bulb&&latest.Exposure.Phase==ExposurePhase.Holding;recover.Enabled=free&&s==SessionState.Unconfirmed;open.Enabled=latest?.IsSuccess==true;}
    async void OnClosing(object? sender,FormClosingEventArgs e){
        if(allowClose)return;e.Cancel=true;if(closing)return;closing=true;stopSequence=true;UpdateControls();
        try{
            if(client!=null){
                if(shooting){WriteLog("正在关闭：请求取消并等待相机收尾，请勿拔线。");try{await client.CancelAsync();}catch(Exception ex){WriteLog(ex.ToString());}if(activeTask!=null)await activeTask;}
                if(activeOperation!=null){try{await activeOperation;}catch(Exception ex){WriteLog(ex.ToString());}}
                bool unconfirmed=client.State==SessionState.Unconfirmed;await client.DisposeAsync();
                if(unconfirmed||client.State==SessionState.Unconfirmed){string message="相机状态未确认。请检查相机曝光和连接状态，按恢复步骤处理。";WriteLog(message);if(!options.Verify)MessageBox.Show(this,message,"关闭提示");else Environment.ExitCode=1;}
            }
        }catch(Exception ex){WriteLog(ex.ToString());Environment.ExitCode=1;if(!options.Verify)MessageBox.Show(this,"关闭时发生错误，请检查相机状态："+ex.Message,"关闭提示");}
        finally{if(client!=null)client.Log-=OnClientLog;allowClose=true;logWriter.Dispose();Close();}
    }
    sealed record CameraChoice(CameraDescription Camera){public override string ToString()=>$"{Camera.Index}: {Camera.Model} ({Camera.Connection})";}
    static int CountButtons(Control parent){int count=0;foreach(Control control in parent.Controls){if(control is Button)count++;count+=CountButtons(control);}return count;}
    static string StateLabel(SessionState value)=>value switch{SessionState.New=>"尚未初始化",SessionState.Initialized=>"已初始化",SessionState.Connected=>"已连接",SessionState.Capturing=>"拍摄处理中",SessionState.Unconfirmed=>"相机状态未确认（需恢复）",SessionState.Disconnected=>"已断开",SessionState.Closed=>"已关闭",SessionState.Faulted=>"故障",_=>value.ToString()};
}
