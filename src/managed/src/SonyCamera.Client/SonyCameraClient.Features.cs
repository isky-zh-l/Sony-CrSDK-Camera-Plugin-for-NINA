namespace SonyCamera.Client;
public sealed partial class SonyCameraClient {
    bool featuresChecked;
    volatile bool previewEnabled,movieRecording,contentDownloading;
    CameraConnectionMode connectionMode;
    public bool LiveViewEnabled=>previewEnabled;
    /// <summary>Last confirmed command state, not a live poll of the camera. Query RecordingState for authoritative readback.</summary>
    public bool MovieRecording=>movieRecording;
    public CameraConnectionMode ConnectionMode=>connectionMode;
    void ResetFeatureState(){astroSession=Guid.NewGuid();previewEnabled=false;movieRecording=false;contentDownloading=false;connectionMode=CameraConnectionMode.Remote;}
    unsafe void EnsureFeatures(){
        if(!initialized)throw new InvalidOperationException(Text.T("请先初始化"));if(featuresChecked)return;
        var e=Native.New<Native.Error>();int rc;
        uint v,ps,fs,folders,contents,ts;
        try{rc=Native.sw_get_feature_abi(out v,out ps,out fs,out folders,out contents,out ts,ref e);}
        catch(EntryPointNotFoundException ex){throw new NotSupportedException(Text.T("当前原生包只有拍照接口，请显式部署带 Feature ABI 1 的新包"),ex);}
        Check(rc,e);
        if(v!=1||ps!=sizeof(Native.Property)||fs!=sizeof(Native.Frame)||folders!=sizeof(Native.Folder)||contents!=sizeof(Native.Content)||ts!=sizeof(Native.Transfer))throw new InvalidDataException(Text.T("扩展ABI布局不匹配"));featuresChecked=true;
    }
    public Task CheckFeatureAbiAsync()=>Submit(Text.T("扩展ABI检查"),EnsureFeatures);
    /// <summary>Requires disconnected initialized session. Contents mode has no exposure/preview; SDK does not allow changing mode while connected.</summary>
    public Task ConnectAsync(uint index,CameraConnectionMode mode)=>Submit(Text.T("指定模式连接"),()=>{
        EnsureFeatures();if(!Enum.IsDefined(mode))throw new ArgumentOutOfRangeException(nameof(mode));
        if(connected||state==SessionState.Unconfirmed)throw new InvalidOperationException(Text.T("先干净断开并重新枚举，再指定连接模式"));
        var e=Native.New<Native.Error>();int rc=Native.sw_connect_mode(index,(uint)mode,15000,ref e);
        if(rc!=0){if(rc is 3 or 4 or 6){connected=true;state=SessionState.Unconfirmed;}Check(rc,e);}
        connected=true;astroSession=Guid.NewGuid();connectionMode=mode;
        if(mode==CameraConnectionMode.Remote){e=Native.New<Native.Error>();Check(Native.sw_wait_ready(15000,ref e),e);}
        state=SessionState.Connected;
    });
    unsafe PropertyProbe ProbeProperty(CameraProperty property){
        EnsureFeatures();RequireConnected();if(!Enum.IsDefined(property))throw new ArgumentOutOfRangeException(nameof(property));
        var p=Native.New<Native.Property>();var e=Native.New<Native.Error>();
        int rc=Native.sw_get_property((uint)property,ref p,null,0,out uint n,ref e);
        if(rc==5)return new(property,null,Native.Convert(e));Check(rc,e);
        for(int retry=0;retry<4;retry++){
            if(n>131072)throw new InvalidDataException(Text.T("候选列表超出容量限制"));var a=new long[Math.Max(1,n)];
            p=Native.New<Native.Property>();e=Native.New<Native.Error>();
            fixed(long* ptr=a){rc=Native.sw_get_property((uint)property,ref p,ptr,n,out uint count,ref e);if(rc==7){n=count;continue;}if(rc==5)return new(property,null,Native.Convert(e));Check(rc,e);if(count>n)throw new InvalidDataException(Text.T("候选数组越界"));
                return new(property,new(property,p.Readable!=0,p.Writable!=0,p.Current,a.Take((int)count).ToArray(),p.Range!=0,p.Min,p.Max,p.Step),null);}
        }
        throw new InvalidOperationException(Text.T("相机候选列表持续变化，请重试"));
    }
    /// <summary>Unknown/sentinel battery values remain null, never a fabricated zero/full battery.</summary>
    public Task<CameraBattery> GetBatteryAsync()=>Submit(Text.T("电池状态"),()=>{
        var percent=ProbeProperty(CameraProperty.BatteryPercent);var level=ProbeProperty(CameraProperty.BatteryLevel);
        var n=percent.Info?.Current;return new CameraBattery(n is >=0 and <=100?(int)n.Value:null,level.Info?.Current,percent.Available);
    });
    public Task<PropertyProbe> GetPropertyAsync(CameraProperty property)=>Submit(Text.T("读取扩展属性"),()=>ProbeProperty(property));
    /// <summary>Unavailable properties remain separate results. This is not an assertion that every property is supported by every lens/mode.</summary>
    public Task<IReadOnlyList<PropertyProbe>> GetPropertiesAsync()=>Submit<IReadOnlyList<PropertyProbe>>(Text.T("扩展能力清单"),()=>Enum.GetValues<CameraProperty>().Select(ProbeProperty).ToArray());
    public Task<long> SetPropertyAsync(CameraProperty property,long choice)=>Submit(Text.T("写入扩展属性"),()=>{
        EnsureFeatures();RequireConnected(true);if(!Enum.IsDefined(property))throw new ArgumentOutOfRangeException(nameof(property));var e=Native.New<Native.Error>();Check(Native.sw_set_property((uint)property,choice,15000,out var actual,ref e),e,true);return actual;
    });
    /// <summary>Aperture in f-number; no nearest selection. Use camera candidates divided by 100 to populate UI.</summary>
    public Task<double> SetApertureAsync(double fNumber){
        if(!double.IsFinite(fNumber)||fNumber<=0||fNumber>1000||Math.Abs(fNumber*100-Math.Round(fNumber*100))>1e-8)throw new ArgumentOutOfRangeException(nameof(fNumber));
        return Set();async Task<double> Set()=>await SetPropertyAsync(CameraProperty.Aperture,(long)Math.Round(fNumber*100)).ConfigureAwait(false)/100.0;
    }
    public Task StartLiveViewAsync()=>Submit(Text.T("启用实时预览"),()=>{EnsureFeatures();RequireConnected(true);var e=Native.New<Native.Error>();Check(Native.sw_live_view_set(1,ref e),e,true);previewEnabled=true;});
    public Task StopLiveViewAsync()=>Submit(Text.T("停止实时预览"),()=>{EnsureFeatures();RequireConnected();var e=Native.New<Native.Error>();Check(Native.sw_live_view_set(0,ref e),e,true);previewEnabled=false;});
    /// <summary>Pull one owned JPEG. Null means SDK currently has no frame. Caller controls polling rate; stop before starting still exposures.</summary>
    public Task<LiveViewFrame?> GetLiveViewFrameAsync(CancellationToken cancellationToken=default)=>Submit(Text.T("实时预览帧"),()=>ReadFrame(cancellationToken));
    unsafe LiveViewFrame? ReadFrame(CancellationToken token){
        EnsureFeatures();RequireConnected();token.ThrowIfCancellationRequested();
        var frame=Native.New<Native.Frame>();var e=Native.New<Native.Error>();int rc=Native.sw_live_view_read(null,0,ref frame,ref e);if(rc==8)return null;Check(rc,e);
        for(int retry=0;retry<4;retry++){
            if(frame.Bytes==0||frame.Bytes>32*1024*1024)throw new InvalidDataException(Text.T("预览容量异常"));var a=new byte[frame.Bytes];var capacity=frame.Bytes;
            frame=Native.New<Native.Frame>();e=Native.New<Native.Error>();token.ThrowIfCancellationRequested();
            fixed(byte* ptr=a){rc=Native.sw_live_view_read(ptr,capacity,ref frame,ref e);if(rc==7)continue;if(rc==8)return null;Check(rc,e);if(frame.Bytes>capacity)throw new InvalidDataException(Text.T("预览数组越界"));}
            if(frame.Bytes!=capacity)Array.Resize(ref a,(int)frame.Bytes);return new(a,frame.Width,frame.Height,frame.Number,frame.TimeCode,DateTimeOffset.UtcNow);
        }
        throw new InvalidOperationException(Text.T("预览帧大小持续变化，请重试"));
    }
    public Task StartMovieAsync()=>Submit(Text.T("开始机内录像"),()=>{EnsureFeatures();RequireConnected(true);var e=Native.New<Native.Error>();Check(Native.sw_movie_set(1,15000,ref e),e,true);movieRecording=true;});
    public Task StopMovieAsync()=>Submit(Text.T("停止机内录像"),()=>{EnsureFeatures();RequireConnected();var e=Native.New<Native.Error>();Check(Native.sw_movie_set(0,15000,ref e),e,true);movieRecording=false;});
    public Task FocusPulseAsync(int speed,TimeSpan duration)=>Submit(Text.T("镜头相对对焦"),()=>{EnsureFeatures();RequireConnected(true);var ms=duration.TotalMilliseconds;if(!double.IsFinite(ms)||ms<1||ms>2000||ms!=Math.Truncate(ms))throw new ArgumentOutOfRangeException(nameof(duration));var e=Native.New<Native.Error>();Check(Native.sw_focus_pulse(speed,(uint)ms,ref e),e,true);});
    public Task<long> SetFocusPositionAsync(ushort position)=>SetPropertyAsync(CameraProperty.FocusPosition,position);
    public Task<IReadOnlyList<CameraFolder>> GetFoldersAsync()=>Submit<IReadOnlyList<CameraFolder>>(Text.T("卡内日期目录"),Folders);
    unsafe IReadOnlyList<CameraFolder> Folders(){
        EnsureFeatures();RequireConnected();var e=Native.New<Native.Error>();Check(Native.sw_get_folders(null,0,out uint n,ref e),e);
        for(int i=0;i<4;i++){if(n>100000)throw new InvalidDataException(Text.T("目录过多"));var a=new Native.Folder[Math.Max(n,1)];fixed(Native.Folder* ptr=a){e=Native.New<Native.Error>();int rc=Native.sw_get_folders(ptr,n,out uint count,ref e);if(rc==7){n=count;continue;}Check(rc,e);if(count>n)throw new InvalidDataException(Text.T("目录数组越界"));return a.Take((int)count).Select(Native.Convert).ToArray();}}
        throw new InvalidOperationException(Text.T("目录列表持续变化"));
    }
    public Task<IReadOnlyList<CameraContent>> GetContentsAsync(CameraFolder folder)=>Submit<IReadOnlyList<CameraContent>>(Text.T("卡内文件"),()=>Contents(folder));
    unsafe IReadOnlyList<CameraContent> Contents(CameraFolder folder){
        ArgumentNullException.ThrowIfNull(folder);EnsureFeatures();RequireConnected();var e=Native.New<Native.Error>();Check(Native.sw_get_contents(folder.Session,folder.Handle,null,0,out uint n,ref e),e);
        for(int i=0;i<4;i++){if(n>1000000)throw new InvalidDataException(Text.T("文件过多"));var a=new Native.Content[Math.Max(n,1)];fixed(Native.Content* ptr=a){e=Native.New<Native.Error>();int rc=Native.sw_get_contents(folder.Session,folder.Handle,ptr,n,out uint count,ref e);if(rc==7){n=count;continue;}Check(rc,e);if(count>n)throw new InvalidDataException(Text.T("内容数组越界"));return a.Take((int)count).Select(Native.Convert).ToArray();}}
        throw new InvalidOperationException(Text.T("文件列表持续变化"));
    }
    /// <summary>Original camera file, including movie, copied to exclusive folder. Returned failure snapshot never contains a successful path.
    /// Cancellation after request yields failed snapshot and quarantines session; clean disconnect before continuing. Does not promise hardware transfer is stopped.</summary>
    public async Task<ContentTransfer> DownloadContentAsync(CameraContent source,string receiveRoot,TimeSpan timeout,IProgress<ContentTransfer>? progress=null,CancellationToken cancellationToken=default){
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(receiveRoot);
        var duration=timeout.TotalMilliseconds;if(!double.IsFinite(duration)||duration<1||duration>3600000||duration!=Math.Truncate(duration))throw new ArgumentOutOfRangeException(nameof(timeout));
        var snapshot=await Submit(Text.T("开始原文件下载"),()=>{
            EnsureFeatures();RequireConnected();if(state!=SessionState.Connected||contentDownloading)throw new InvalidOperationException(Text.T("当前有任务或未确认状态"));cancellationToken.ThrowIfCancellationRequested();
            if(receiveRoot.Contains('\0'))throw new ArgumentException(Text.T("路径含NUL"),nameof(receiveRoot));
            var p=Native.New<Native.Transfer>();var e=Native.New<Native.Error>();int rc=Native.sw_content_start(source.Session,source.Handle,receiveRoot,(uint)duration,ref p,ref e);
            if(rc!=0){Check(rc,e,true);}contentDownloading=true;return Native.Convert(p);
        }).ConfigureAwait(false);
        try{
            while(snapshot.State==ContentTransferState.Waiting){
                await Task.Delay(20,CancellationToken.None).ConfigureAwait(false);
                snapshot=await Submit(Text.T("原文件下载进度"),()=>{
                    var p=Native.New<Native.Transfer>();var e=Native.New<Native.Error>();int rc=cancellationToken.IsCancellationRequested?Native.sw_content_cancel(snapshot.Id,ref p,ref e):Native.sw_content_poll(snapshot.Id,ref p,ref e);
                    if(p.Id==0){Check(rc,e);throw new InvalidDataException(Text.T("下载未返回匹配任务"));}
                    var result=Native.Convert(p);if(result.State==ContentTransferState.Failed)state=SessionState.Unconfirmed;return result;
                }).ConfigureAwait(false);
                if(progress!=null){var copy=snapshot;Notify(()=>progress.Report(copy));}
            }
            return snapshot;
        }finally{
            // Runs on native owner. Disposal can have already closed the command queue.
            try{await Submit(Text.T("结束下载等待"),()=>{contentDownloading=false;}).ConfigureAwait(false);}catch(ObjectDisposedException){}
        }
    }
}
