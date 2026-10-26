using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using SonyCamera.Client;
namespace SonyA7V.Camera;
public sealed partial class SonyCameraService {
    private readonly CropMonitor cropMonitor=new();
    private CropObservation exposureCrop=new(SensorFrame.Unknown,null);
    public CropObservation Crop=>cropMonitor.Current;
    public SensorFrame CaptureFrame {get;private set;}=SensorFrame.Unknown;
    public string CropWarning {get;private set;}="";
    private async Task RefreshCropCoreAsync() {
        long? mode=null;
        try {var probe=await client!.GetPropertyAsync(CameraProperty.ApsCMode).ConfigureAwait(false);if(probe.Info is {Readable:true} info)mode=info.Current;}
        catch(Exception ex){Logger.Trace("[SonyCrSDK] Crop mode unavailable: "+ex.Message);}
        PublishCrop(CropMonitor.FromBody(Model,mode));
    }
    private void PublishCrop(CropObservation observation) {
        var notify=cropMonitor.Observe(observation);
        CropWarning=observation.State==SensorFrame.ApsC
            ?Text.T("APS-C / Super 35 crop mode is active. Confirm this is intended and check image dimensions, field of view and plate-solving settings.")
            :observation.State==SensorFrame.Unknown?Text.T("Sensor mode is unknown. Image dimensions alone do not establish APS-C mode."):"";
        if(notify) {
            var message=CropWarning.Length>0?CropWarning:Text.T("Sensor mode changed to full frame. Check image dimensions, field of view and plate-solving settings.");
            Logger.Info("[SonyCrSDK] "+message);
            // NINA notifications are nonmodal; repeated frames in the same mode produce no new toast.
            if(System.Windows.Application.Current is {} app)app.Dispatcher.BeginInvoke(new Action(()=>Notification.ShowWarning(message)));
        }
    }
    private void VerifyRawCrop(RawInfo raw) {
        var verified=CropMonitor.VerifyRaw(exposureCrop,Model,raw,LastCapture?.RawFormat??0);
        CaptureFrame=verified.State;
        if(verified.State!=exposureCrop.State) {
            PublishCrop(verified);
            Logger.Warning("[SonyCrSDK] Body sensor mode disagrees with model-specific RAW evidence; frame mode is unknown");
        } else if(Crop.BodyMode==exposureCrop.BodyMode)PublishCrop(verified);
    }
}
