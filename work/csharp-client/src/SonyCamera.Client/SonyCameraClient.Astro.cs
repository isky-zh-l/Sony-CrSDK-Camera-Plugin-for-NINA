namespace SonyCamera.Client;
public sealed partial class SonyCameraClient {
    Guid astroSession=Guid.NewGuid();
    long WriteProperty(CameraProperty property,long value) {
        var e=Native.New<Native.Error>();Check(Native.sw_set_property((uint)property,value,15000,out var actual,ref e),e,true);return actual;
    }
    /// <summary>Preflight every requested setting before the first write. One owner command prevents
    /// interleaving with capture. Missing/read-only/conflicting settings fail explicitly, never skip.</summary>
    public Task<AstroPreparationReceipt> PrepareAstroSessionAsync(AstroPreparationOptions options)=>Submit("天文拍摄准备",()=>{
        ArgumentNullException.ThrowIfNull(options);EnsureFeatures();RequireConnected(true);
        if(previewEnabled||movieRecording)throw new InvalidOperationException("Stop preview/movie before preparing astronomy settings");
        var plan=new List<AstroSettingChange>();
        foreach(var setting in options.Requested()) {
            var e=Native.New<Native.Error>();int rc;
            uint id;long target;
            try {rc=Native.sw_get_astro_target((uint)setting,out id,out target,ref e);}
            catch(EntryPointNotFoundException ex){throw new NotSupportedException("Deploy the complete astronomy native candidate before using preparation",ex);}
            Check(rc,e);var property=(CameraProperty)id;
            var p=ProbeProperty(property).Info??throw new NotSupportedException($"{setting}: property unavailable in current mode/lens");
            if(!p.Readable||(p.Current!=target&&!p.Accepts(target)))throw new NotSupportedException($"{setting}: requested value is not a current writable candidate");
            plan.Add(new(setting,property,p.Current,target,false,p.Current==target));
        }
        try {
            for(int i=0;i<plan.Count;i++) {
                var change=plan[i];if(change.Before==change.Requested)continue;
                // Record submission before the call: failure can mean the camera accepted an unconfirmed write.
                plan[i]=change with{Submitted=true};
                if(WriteProperty(change.Property,change.Requested)!=change.Requested)throw new InvalidOperationException("Native readback does not match requested value");
                plan[i]=plan[i] with{Confirmed=true};
            }
        }catch(Exception ex){throw new AstroPreparationException(new(astroSession,plan),ex);}
        return new AstroPreparationReceipt(astroSession,plan);
    });
    /// <summary>Explicit restore only, on the same clean session, after preflighting all old/current
    /// values. Does not overwrite subsequent user changes or guess after disconnect/write failure.</summary>
    public Task RestoreAstroSessionAsync(AstroPreparationReceipt receipt)=>Submit("恢复天文准备设置",()=>{
        ArgumentNullException.ThrowIfNull(receipt);EnsureFeatures();RequireConnected(true);
        if(receipt.Session!=astroSession||receipt.Restored)throw new InvalidOperationException("Receipt is stale or already restored");
        if(previewEnabled||movieRecording)throw new InvalidOperationException("Stop preview/movie before restoring settings");
        if(receipt.Changes.Any(x=>x.Submitted&&!x.Confirmed))throw new InvalidOperationException("Cannot restore an unconfirmed preparation receipt");
        var changes=receipt.Changes.Where(x=>x.Submitted).Reverse().ToArray();
        foreach(var change in changes) {
            var p=ProbeProperty(change.Property).Info;
            if(p is null||!p.Readable||p.Current!=change.Requested||!p.Accepts(change.Before))
                throw new InvalidOperationException($"{change.Property}: state changed or original value no longer writable; no restore submitted");
        }
        foreach(var change in changes)WriteProperty(change.Property,change.Before);
        receipt.Restored=true;
    });
    LensFocusStatus ReadLensFocus() {
        var target=ProbeProperty(CameraProperty.FocusPosition).Info;
        var actual=ProbeProperty(CameraProperty.FocusPositionActual).Info;
        var driving=ProbeProperty(CameraProperty.FocusDrivingStatus).Info;
        var mode=ProbeProperty(CameraProperty.FocusMode).Info;
        var e=Native.New<Native.Error>();Check(Native.sw_get_astro_target((uint)AstroSetting.ManualLensFocus,out _,out var mf,ref e),e);
        if(target is not {Readable:true,Writable:true,IsRange:true}||actual is not {Readable:true}||
           driving is not {Readable:true,Current:1 or 2}||mode is not {Readable:true}||mode.Current!=mf||
           target.Minimum<0||target.Maximum>ushort.MaxValue||target.Step<=0||actual.Current<target.Minimum||actual.Current>target.Maximum)
            throw new NotSupportedException("Absolute lens focus needs a writable position range, actual position, driving status and manual focus; telescope-only rigs do not provide this");
        return new(target,actual.Current,driving.Current==2);
    }
    public Task<LensFocusStatus> GetLensFocusAsync()=>Submit("镜头绝对对焦状态",()=>{EnsureFeatures();RequireConnected();return ReadLensFocus();});
    /// <summary>Exact SDK position units, not micrometres. No nearest selection. Once submitted,
    /// cancellation cannot prove a physical stop; wait for readback and idle before returning cancellation.</summary>
    public Task<LensFocusStatus> MoveLensFocusAsync(long position,CancellationToken cancellationToken=default)=>Submit("镜头绝对对焦",()=>{
        EnsureFeatures();RequireConnected(true);cancellationToken.ThrowIfCancellationRequested();
        if(previewEnabled)throw new InvalidOperationException("Stop preview before moving lens focus");
        var before=ReadLensFocus();if(before.Driving)throw new InvalidOperationException("Lens is already moving");
        if(!before.Target.Accepts(position))throw new ArgumentOutOfRangeException(nameof(position));
        WriteProperty(CameraProperty.FocusPosition,position);
        var end=DateTime.UtcNow+TimeSpan.FromSeconds(15);
        do {
            LensFocusStatus current;
            try {current=ReadLensFocus();}catch {state=SessionState.Unconfirmed;throw;}
            if(!current.Driving&&current.Position==position){cancellationToken.ThrowIfCancellationRequested();return current;}
            Thread.Sleep(20);
        }while(DateTime.UtcNow<end);
        state=SessionState.Unconfirmed;throw new InvalidOperationException("Lens idle/position unconfirmed; reconnect before further operations");
    });
}
