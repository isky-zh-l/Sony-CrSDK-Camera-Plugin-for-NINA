using System.Collections.Concurrent;
using System.Diagnostics;

namespace SonyCamera.Client;

/// <summary>One client per process, one fixed native owner thread. Native libraries remain loaded until process exit.</summary>
public sealed partial class SonyCameraClient : IAsyncDisposable {
    readonly NativeDeploymentOptions deployment;
    readonly object submissionGate=new();
    readonly BlockingCollection<Action> commands=new(); readonly Thread owner;
    readonly TaskCompletionSource stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    static int instanceCreated;
    volatile SessionState state=SessionState.New;
    volatile CaptureSnapshot? lastCapture; volatile AbiReport? abi;
    bool initialized,connected,captureQuarantined; int disposing;
    readonly ConcurrentQueue<Action> notifications=new(); int notificationWorker;
    PendingCapture? pending;
    sealed class PendingCapture {
        internal readonly TaskCompletionSource<CaptureSnapshot> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IProgress<CaptureSnapshot>? Progress; internal CancellationTokenRegistration Registration;
        internal int CancelRequested; internal ulong Id;
    }
    public SonyCameraClient(NativeDeploymentOptions deployment) {
        ArgumentNullException.ThrowIfNull(deployment);
        NativePackageValidator.RequireHash(deployment.ApprovedManifestSha256,nameof(deployment.ApprovedManifestSha256));
        if(!Path.IsPathFullyQualified(deployment.ManifestPath))throw new ArgumentException(Text.T("ManifestPath必须是绝对路径"),nameof(deployment));
        if(Interlocked.CompareExchange(ref instanceCreated,1,0)!=0) throw new InvalidOperationException(Text.T("同一进程只能创建一个SonyCameraClient；正常重连请复用当前对象"));
        this.deployment=deployment;
        owner=new Thread(OwnerLoop){IsBackground=true,Name="Sony native owner"}; owner.Start();
    }
    public SessionState State=>state;
    public CaptureSnapshot? LastCapture=>lastCapture;
    public AbiReport? Abi=>abi;
    public event Action<ClientLog>? Log;
    void WriteLog(string operation,string message) {
        var handlers=Log; if(handlers==null)return;
        var entry=new ClientLog(DateTimeOffset.Now,Environment.CurrentManagedThreadId,operation,message);
        Notify(()=>{foreach(Action<ClientLog> handler in handlers.GetInvocationList())try{handler(entry);}catch{}});
    }
    void Notify(Action callback) {
        notifications.Enqueue(callback);
        if(Interlocked.CompareExchange(ref notificationWorker,1,0)!=0)return;
        ThreadPool.QueueUserWorkItem(_=>{
            do {
                while(notifications.TryDequeue(out var next))try{next();}catch{}
                Interlocked.Exchange(ref notificationWorker,0);
            }while(!notifications.IsEmpty&&Interlocked.CompareExchange(ref notificationWorker,1,0)==0);
        });
    }
    Task<T> Submit<T>(string name,Func<T> action) {
        lock(submissionGate) {
        if(disposing!=0) return Task.FromException<T>(new ObjectDisposedException(nameof(SonyCameraClient)));
        var result=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { commands.Add(()=>{try { var value=action(); WriteLog(name,Text.T("完成")); result.TrySetResult(value); } catch(Exception ex){ if(ex is not (CameraException or InvalidOperationException or ArgumentException or OperationCanceledException or FileNotFoundException or DirectoryNotFoundException or NotSupportedException) && initialized)state=SessionState.Unconfirmed; WriteLog(name,ex.Message); result.TrySetException(ex); }}); }
        catch(InvalidOperationException){result.TrySetException(new ObjectDisposedException(nameof(SonyCameraClient)));}
        return result.Task;
        }
    }
    Task Submit(string name,Action action)=>Submit(name,()=>{action();return true;});
    void OwnerLoop() {
        try {
            while(!commands.IsCompleted) {
                if(commands.TryTake(out var command,pending==null?100:0)) command();
                if(pending!=null) {
                    try { if(Interlocked.Exchange(ref pending.CancelRequested,0)!=0) ControlCapture(false); else Poll(); }
                    catch(Exception ex) {state=SessionState.Unconfirmed;captureQuarantined=true;var p=pending; pending=null;p?.Registration.Dispose();p?.Completion.TrySetException(ex);WriteLog(Text.T("轮询"),ex.ToString());}
                }
            }
        } catch(Exception ex){state=SessionState.Faulted;WriteLog(Text.T("所有者线程"),ex.ToString());stopped.TrySetException(ex);return;}
        stopped.TrySetResult();
    }
    void RequireConnected(bool writable=false) {
        if(!connected)throw new InvalidOperationException(Text.T("请先连接相机"));
        if(writable&&(state!=SessionState.Connected||connectionMode==CameraConnectionMode.Contents||contentDownloading))throw new InvalidOperationException(Text.T("当前状态不允许修改参数或开始新拍摄；请等待任务完成或显式恢复"));
    }
    static CameraException ExceptionFor(Native.Error error)=>new(Native.Convert(error));
    void Check(int code,Native.Error error,bool write=false) {
        if(code==0)return;
        if(connected && code is 3 or 4 or 6)state=SessionState.Unconfirmed;
        if(write && (error.Stage==10 || error.Id!=0 || code is 3 or 4 or 6))state=SessionState.Unconfirmed;
        throw ExceptionFor(error);
    }
    public Task InitializeAsync()=>Submit(Text.T("初始化"),()=>{
        if(initialized)return;
        var sizes=new Dictionary<string,int>();var offsets=new Dictionary<string,int>();NativeLoader.CheckLayout(sizes,offsets);
        var package=NativeLoader.Load(deployment);WriteLog(Text.T("运行环境"),Text.F($"包={package.PackageId}, manifest={package.ManifestSha256}, backend={package.Backend}；")+(package.Backend=="test"?Text.T("TEST ONLY：隔离替身，不是实机证据"):Text.T("生产包一致性已按宿主外部批准清单验证；哈希不提供来源认证")));
        var e=Native.New<Native.Error>();Check(Native.sw_get_abi_info(out var version,out var es,out var cs,out var ps,out var crs,ref e),e);
        e=Native.New<Native.Error>();Check(Native.sw_get_exposure_abi_sizes(out var rs,out var caps,out var infos,ref e),e);
        if(version!=2||es!=184||cs!=168||ps!=32||crs!=2304||rs!=40||caps!=56||infos!=464)throw new InvalidDataException(Text.F($"运行时ABI不匹配：version={version}, sizes={es}/{cs}/{ps}/{crs}/{rs}/{caps}/{infos}"));
        var wrapper=package.ValidatedFiles.Single(file=>file.Role=="wrapper");
        abi=new(version,wrapper.Sha256,wrapper.FullPath,sizes,offsets);
        e=Native.New<Native.Error>();Check(Native.sw_initialize(ref e),e);initialized=true;state=SessionState.Initialized;
    });
    public Task<IReadOnlyList<CameraDescription>> EnumerateAsync()=>Submit<IReadOnlyList<CameraDescription>>(Text.T("枚举"),Enumerate);
    unsafe IReadOnlyList<CameraDescription> Enumerate() {
        if(!initialized)throw new InvalidOperationException(Text.T("请先初始化"));
        var e=Native.New<Native.Error>();Check(Native.sw_enumerate(out var count,ref e),e);if(count>1024)throw new InvalidDataException(Text.T("枚举数量异常"));
        var list=new List<CameraDescription>();for(uint i=0;i<count;i++){var c=Native.New<Native.Camera>();e=Native.New<Native.Error>();Check(Native.sw_camera_info(i,ref c,ref e),e);list.Add(new(c.Index,Native.Utf8(c.Model,128),Native.Utf8(c.Connection,32)));}return list;
    }
    public Task ConnectAsync(uint index)=>Submit(Text.T("连接"),()=>{
        if(!initialized||connected||state==SessionState.Unconfirmed)throw new InvalidOperationException(Text.T("当前不能连接，请检查初始化和未确认状态"));
        var e=Native.New<Native.Error>();int code=Native.sw_connect(index,15000,ref e);
        if(code!=0) {
            // Native Connect can own a partial handle. Never infer cleanup from a timeout code.
            var cleanup=Native.New<Native.Error>();var cleanupCode=Native.sw_disconnect(15000,ref cleanup);
            connected=cleanupCode!=0;state=cleanupCode==0?SessionState.Initialized:SessionState.Unconfirmed;
            if(cleanupCode!=0)WriteLog("Connection cleanup",Native.Convert(cleanup).ToString());
            throw ExceptionFor(e);
        }
        connected=true;astroSession=Guid.NewGuid();
        e=Native.New<Native.Error>();code=Native.sw_wait_ready(15000,ref e);if(code!=0){state=SessionState.Unconfirmed;Check(code,e);}state=SessionState.Connected;
    });
    /// <summary>
    /// After the device was physically lost (cable pulled) the native session is released but the
    /// capture can never be verified. A fresh connection re-runs every capture precondition, so the
    /// quarantine bookkeeping is dropped instead of blocking reconnection until the process restarts.
    /// </summary>
    /// <summary>
    /// Recreates the SDK session. Needed when the USB device disappeared and came back: the old
    /// session may no longer see it even though enumeration is allowed again.
    /// </summary>
    public Task RestartSdkAsync()=>Submit(Text.T("重启SDK"),()=>{
        if(!initialized) throw new InvalidOperationException(Text.T("SDK 尚未初始化"));
        if(connected) throw new InvalidOperationException(Text.T("设备仍连接，不能重启 SDK"));
        var e=Native.New<Native.Error>();
        var rc=Native.sw_shutdown(ref e);
        if(rc!=0) Check(rc,e);
        initialized=false; state=SessionState.New;
        e=Native.New<Native.Error>();
        Check(Native.sw_initialize(ref e),e);
        initialized=true; state=SessionState.Initialized;
        WriteLog(Text.T("重启SDK"),Text.T("SDK 会话已重建"));
    });
    public void ResetForReconnect(){
        // The caller only reaches this after the device was reported released; never dead-end here.
        if(connected) throw new InvalidOperationException(Text.T("Device cleanup is not confirmed; disconnect must succeed before reset"));
        pending=null; lastCapture=null; captureQuarantined=false; state=SessionState.Initialized;
        WriteLog(Text.T("重连"),Text.T("设备已释放，清理未确认拍摄状态以便重新连接"));
    }
    public Task DisconnectAsync()=>Submit(Text.T("断开"),()=>Disconnect());
    void Disconnect(bool cancelActive=true){if(!connected)return;if(cancelActive && pending!=null)ControlCapture(false);var e=Native.New<Native.Error>();int rc=Native.sw_disconnect(15000,ref e);if(rc==0){connected=false;ResetFeatureState();state=captureQuarantined?SessionState.Unconfirmed:SessionState.Disconnected;}else {state=SessionState.Unconfirmed;if(rc==(int)ProjectCode.Cancelled && e.Stage==6 && e.Id!=0){connected=false;captureQuarantined=true;}}Check(rc,e);}
    public Task<CameraParameters> ReadParametersAsync()=>Submit(Text.T("读取参数"),()=>{RequireConnected();var p=Native.New<Native.Parameters>();var e=Native.New<Native.Error>();Check(Native.sw_read_parameters(ref p,ref e),e);return Native.Convert(p);});
    public Task<IReadOnlyList<uint>> GetSupportedIsoAsync()=>Submit<IReadOnlyList<uint>>(Text.T("ISO候选"),IsoCandidates);
    unsafe IReadOnlyList<uint> IsoCandidates(){RequireConnected();var e=Native.New<Native.Error>();Check(Native.sw_get_supported_iso(null,0,out uint n,ref e),e);for(int retry=0;retry<4;retry++){if(n>100000)throw new InvalidDataException(Text.T("ISO候选容量异常"));var a=new uint[n];fixed(uint* p=a){e=Native.New<Native.Error>();int rc=Native.sw_get_supported_iso(p,n,out uint count,ref e);if(rc==7){n=count;continue;}Check(rc,e);if(count>n)throw new InvalidDataException(Text.T("ISO候选越界"));return a.Take((int)count).ToArray();}}throw new InvalidOperationException(Text.T("ISO候选持续变化，请重试"));}
    public Task<IReadOnlyList<double>> GetSupportedExposureSecondsAsync()=>Submit<IReadOnlyList<double>>(Text.T("快门候选"),ExposureCandidates);
    unsafe IReadOnlyList<double> ExposureCandidates(){RequireConnected();var e=Native.New<Native.Error>();Check(Native.sw_get_supported_exposure_seconds(null,0,out uint n,ref e),e);for(int retry=0;retry<4;retry++){if(n>100000)throw new InvalidDataException(Text.T("快门候选容量异常"));var a=new double[n];fixed(double* p=a){e=Native.New<Native.Error>();int rc=Native.sw_get_supported_exposure_seconds(p,n,out uint count,ref e);if(rc==7){n=count;continue;}Check(rc,e);if(count>n)throw new InvalidDataException(Text.T("快门候选越界"));return a.Take((int)count).ToArray();}}throw new InvalidOperationException(Text.T("快门候选持续变化，请重试"));}
    public Task<uint> SetIsoAsync(uint iso)=>Submit(Text.T("设置ISO"),()=>{RequireConnected(true);var e=Native.New<Native.Error>();Check(Native.sw_set_iso(iso,15000,out var actual,ref e),e,true);return actual;});
    public Task<double> SetExposureSecondsAsync(double seconds)=>Submit(Text.T("设置快门"),()=>{RequireConnected(true);var e=Native.New<Native.Error>();Check(Native.sw_set_exposure_seconds(seconds,15000,out var actual,ref e),e,true);return actual;});
    public Task<SaveDestination> GetSaveDestinationAsync()=>Submit(Text.T("读取保存位置"),()=>{RequireConnected();var e=Native.New<Native.Error>();Check(Native.sw_get_save_destination(out var value,ref e),e);return (SaveDestination)value;});
    public Task<SaveDestination> SetSaveDestinationAsync(SaveDestination destination)=>Submit(Text.T("设置保存位置"),()=>{RequireConnected(true);var e=Native.New<Native.Error>();Check(Native.sw_set_save_destination((uint)destination,15000,out var actual,ref e),e,true);return (SaveDestination)actual;});
    public Task<ExposureCapabilities> GetExposureCapabilitiesAsync()=>Submit(Text.T("曝光能力"),()=>{RequireConnected();var c=Native.New<Native.Capabilities>();var e=Native.New<Native.Error>();Check(Native.sw_get_exposure_capabilities(ref c,ref e),e);return new ExposureCapabilities(c.Candidate!=0,c.Min,c.Max,c.Step,c.MaxExposure,c.MaxDownload,c.TimerReadable!=0,c.TimerEnabled!=0);});
    public Task CheckCaptureReadyAsync()=>Submit(Text.T("拍摄就绪"),()=>{RequireConnected(true);var e=Native.New<Native.Error>();Check(Native.sw_check_capture_ready(ref e),e);});
    public Task<CaptureSnapshot> CaptureAsync(string receiveRoot,string prefix,ExposureRequest request,IProgress<CaptureSnapshot>? progress=null,CancellationToken cancellationToken=default) {
        var p=new PendingCapture{Progress=progress};
        return StartCaptureAndWait();
        async Task<CaptureSnapshot> StartCaptureAndWait(){await Submit(Text.T("开始拍摄"),()=>{
            RequireConnected(true);cancellationToken.ThrowIfCancellationRequested();
            if(receiveRoot.IndexOf('\0')>=0||prefix.IndexOf('\0')>=0)throw new ArgumentException(Text.T("路径/前缀不得含NUL"));
            var r=Native.New<Native.Request>();r.Mode=(uint)request.Mode;r.Seconds=request.Seconds;r.ExposureTimeout=request.ExposureTimeoutSeconds;r.DownloadTimeout=request.DownloadTimeoutSeconds;r.Setup=request.SetupTimeoutMilliseconds;
            var expectedIso=request.ExpectedIso;
            if(expectedIso==0) {var actual=Native.New<Native.Parameters>();var readError=Native.New<Native.Error>();Check(Native.sw_read_parameters(ref actual,ref readError),readError);if(actual.IsoKind!=(uint)IsoKind.Fixed)throw new InvalidOperationException(Text.T("Capture requires fixed ISO"));expectedIso=actual.Iso;}
            var c=Native.New<Native.Capture>();var e=Native.New<Native.Error>();int rc=Native.sw_capture_start_verified(receiveRoot,string.IsNullOrEmpty(prefix)?null:prefix,ref r,expectedIso,request.RequireFullFrame?1u:0u,ref c,ref e);
            if(c.Id==0){Check(rc,e,true);throw new InvalidDataException(Text.T("原生开始拍摄未返回编号"));}
            p.Id=c.Id;pending=p;state=SessionState.Capturing;
            p.Registration=cancellationToken.Register(()=>Interlocked.Exchange(ref p.CancelRequested,1));
            Accept(c,rc==0?8:rc);
        }).ConfigureAwait(false);return await p.Completion.Task.ConfigureAwait(false);}
    }
    ExposureDetails? ExposureInfo(ulong id){var info=Native.New<Native.Exposure>();var e=Native.New<Native.Error>();int rc=Native.sw_capture_exposure_info(id,ref info,ref e);if(rc==0)return Native.Convert(info);WriteLog(Text.T("曝光信息"),Native.Convert(e).ToString());return null;}
    CaptureSnapshot Snapshot(Native.Capture c,int rc)=>Native.Convert(c,rc,ExposureInfo(c.Id));
    void Accept(Native.Capture c,int rc) {
        var snap=Snapshot(c,rc);lastCapture=snap;var p=pending;
        if(p?.Progress!=null) {var sink=p.Progress;Notify(()=>sink.Report(snap));}
        if(snap.IsPending)return;
        captureQuarantined=!snap.IsSuccess;state=snap.IsSuccess?SessionState.Connected:SessionState.Unconfirmed;
        pending=null;p?.Registration.Dispose();p?.Completion.TrySetResult(snap);
        WriteLog(Text.T("拍摄终态"),$"id={snap.CaptureId}, code={snap.Code}, state={snap.State}, bytes={snap.FileBytes}, path={snap.ArwPath}, failure={snap.Failure}");
    }
    void Poll(){var p=pending!;var c=Native.New<Native.Capture>();var e=Native.New<Native.Error>();int rc=Native.sw_capture_wait(p.Id,50,ref c,ref e);if(c.Id==0){state=SessionState.Unconfirmed;throw ExceptionFor(e);}Accept(c,rc);}
    CaptureSnapshot ControlCapture(bool end) {
        RequireConnected();var id=pending?.Id??lastCapture?.CaptureId??0;var c=Native.New<Native.Capture>();var e=Native.New<Native.Error>();int rc=end?Native.sw_capture_end_exposure(id,ref c,ref e):Native.sw_capture_cancel(id,ref c,ref e);
        if(c.Id==0) {Check(rc,e);throw new InvalidDataException(Text.T("取消/结束未返回任务"));}
        Accept(c,rc);return lastCapture!;
    }
    public Task<CaptureSnapshot> CancelAsync()=>Submit(Text.T("取消拍摄"),()=>ControlCapture(false));
    public Task<CaptureSnapshot> EndExposureAsync()=>Submit(Text.T("提前结束曝光"),()=>ControlCapture(true));
    public Task<CaptureSnapshot> GetCaptureAsync(ulong id)=>Submit(Text.T("查询拍摄"),()=>{RequireConnected();var c=Native.New<Native.Capture>();var e=Native.New<Native.Error>();int rc=Native.sw_capture_wait(id,0,ref c,ref e);if(c.Id==0)throw ExceptionFor(e);var snap=Snapshot(c,rc);if(pending?.Id==id)Accept(c,rc);return snap;});
    public async Task<RecoveryResult> ReconcileAsync(TimeSpan budget) {
        if(budget<=TimeSpan.Zero||budget>TimeSpan.FromMinutes(5))throw new ArgumentOutOfRangeException(nameof(budget));
        var clock=Stopwatch.StartNew();RecoveryResult? result=null;
        do {result=await Submit(Text.T("恢复证据"),ReconcileStep).ConfigureAwait(false);if(result.Recovered||result.Error.Code!=ProjectCode.Pending)return result;}while(clock.Elapsed<budget);
        return result!;
    }
    RecoveryResult ReconcileStep(){RequireConnected();if(pending!=null)throw new InvalidOperationException(Text.T("正在拍摄，不能恢复"));var id=lastCapture?.CaptureId??0;var c=Native.New<Native.Capture>();var e=Native.New<Native.Error>();int rc=Native.sw_capture_reconcile(id,50,ref c,ref e);var snap=c.Id!=0?Snapshot(c,c.State==(uint)CaptureState.Succeeded?0:c.Failure.Code):lastCapture??Empty();if(c.Id!=0)lastCapture=snap;if(rc==0){e=Native.New<Native.Error>();rc=Native.sw_check_capture_ready(ref e);if(rc==0){captureQuarantined=false;state=SessionState.Connected;return new(true,snap,Native.Convert(e));}}state=SessionState.Unconfirmed;return new(false,snap,Native.Convert(e));}
    static CaptureSnapshot Empty()=>new(ProjectCode.State,CaptureState.None,0,null,0,0,0,0,new(ProjectCode.State,0,0,0,Text.T("没有任务")),"","",null);
    public async ValueTask DisposeAsync() {
        lock(submissionGate) {
        if(disposing==0){disposing=1;
        commands.Add(()=>{
            try {
                var cleanupErrors=new List<Exception>();
                void Cleanup(string name,Action action) {try{action();}catch(Exception ex){cleanupErrors.Add(ex);WriteLog(name,ex.ToString());}}
                Cleanup(Text.T("关闭：取消"),()=>{if(pending!=null)ControlCapture(false);});
                Cleanup(Text.T("关闭：恢复"),()=>{
                    if(connected && state==SessionState.Unconfirmed && lastCapture!=null){
                        var sw=Stopwatch.StartNew();
                        while(sw.Elapsed<TimeSpan.FromSeconds(10)){
                            var r=ReconcileStep();
                            if(r.Recovered)break;
                            if(r.Error.Code!=ProjectCode.Pending){cleanupErrors.Add(new CameraException(r.Error));break;}
                        }
                    }
                });
                // Disconnect may report CANCELLED after already releasing the device.
                // Even other disconnect errors must not skip shutdown: only native code
                // can authoritatively decide whether its remaining resources permit it.
                Cleanup(Text.T("关闭：断开"),()=>{if(connected)Disconnect(cancelActive:false);});
                Cleanup(Text.T("关闭：释放SDK"),()=>{
                    if(initialized){var e=Native.New<Native.Error>();Check(Native.sw_shutdown(ref e),e);initialized=false;connected=false;}
                });
                if(captureQuarantined)cleanupErrors.Add(new CameraException(new(ProjectCode.State,0,18,lastCapture?.CaptureId??0,Text.T("SDK资源已尝试释放，但拍摄未取得恢复证据；相机状态仍未确认，请人工检查"))));
                if(cleanupErrors.Count!=0){
                    var primary=cleanupErrors.OfType<CameraException>().FirstOrDefault()?.Error??new CameraError(ProjectCode.Internal,0,7,lastCapture?.CaptureId??0,Text.T("关闭失败"));
                    throw new CameraException(primary with {Message=Text.T("关闭存在未确认或清理错误：")+string.Join(" | ",cleanupErrors.Select(error=>error.Message))});
                }
                state=SessionState.Closed;
            }catch(Exception ex){state=SessionState.Unconfirmed;WriteLog(Text.T("关闭未确认"),ex.ToString());var p=pending;pending=null;p?.Registration.Dispose();p?.Completion.TrySetException(ex);stopped.TrySetException(ex);}
            finally{commands.CompleteAdding();}
        });
        }
        }
        await stopped.Task.ConfigureAwait(false);
    }
}
