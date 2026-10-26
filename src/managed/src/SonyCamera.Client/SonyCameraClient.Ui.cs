namespace SonyCamera.Client;
public sealed partial class SonyCameraClient {
    public Task<IReadOnlyList<PropertyProbe>> InspectPropertiesAsync(IReadOnlyList<CameraProperty> properties) {
        ArgumentNullException.ThrowIfNull(properties);var copy=properties.ToArray();
        if(copy.Length>64||copy.Distinct().Count()!=copy.Length)throw new ArgumentException("Invalid inspection list");
        return Submit<IReadOnlyList<PropertyProbe>>(Text.T("界面参数快照"),()=>{
            EnsureFeatures();RequireConnected(true);
            if(previewEnabled||movieRecording)throw new InvalidOperationException("Stop live view/movie before inspecting settings");
            return copy.Select(ProbeProperty).ToArray();
        });
    }
    public Task<long> SetPropertyIfCurrentAsync(CameraProperty property,long expected,long requested)=>Submit(Text.T("界面参数核对与写入"),()=>{
        EnsureFeatures();RequireConnected(true);
        if(previewEnabled||movieRecording)throw new InvalidOperationException("Stop live view/movie before editing settings");
        var p=ProbeProperty(property).Info??throw new NotSupportedException("Property is unavailable in this mode/lens");
        if(!p.Readable||p.Current!=expected)throw new InvalidOperationException(Text.T("相机参数已改变，请刷新后重新选择；未提交写入"));
        if(p.Current==requested)return p.Current;
        if(!p.Accepts(requested))throw new ArgumentOutOfRangeException(nameof(requested),"Not an exact writable candidate");
        return WriteProperty(property,requested);
    });
}
