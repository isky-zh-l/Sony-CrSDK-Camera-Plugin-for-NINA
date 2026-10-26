using SonyCamera.Client;
namespace SonyA7V.Camera;
public enum SensorFrame { Unknown, FullFrame, ApsC }
public sealed record CropObservation(SensorFrame State,long? BodyMode,bool RawVerified=false);
/// <summary>Body evidence is mandatory. Dimensions alone never classify the sensor mode.</summary>
public sealed class CropMonitor {
    private SensorFrame? previous;
    public CropObservation Current {get;private set;}=new(SensorFrame.Unknown,null);
    public CropObservation Body {get;private set;}=new(SensorFrame.Unknown,null);
    private CropObservation? rawEvidence;
    public void Reset(){previous=null;rawEvidence=null;Body=Current=new(SensorFrame.Unknown,null);}
    public bool ObserveBody(CropObservation observation) {
        // Re-reading the same property does not resolve conflicting RAW evidence.
        if(Body.State!=observation.State || Body.BodyMode!=observation.BodyMode)rawEvidence=null;
        Body=observation;
        return Observe(rawEvidence??Body);
    }
    public bool ObserveRaw(CropObservation observation) {
        // A completed older frame must not replace a new body mode on the panel.
        if(observation.BodyMode!=Body.BodyMode)return false;
        // Only verified RAW evidence can clear an earlier contradiction.
        if(observation.State==SensorFrame.Unknown || observation.RawVerified)rawEvidence=observation;
        return Observe(rawEvidence??Body);
    }
    public static CropObservation FromBody(string model,long? mode) {
        // Only these full-frame sensor identities have model-specific RAW evidence in this repository.
        bool fullFrameBody=CameraSupport.Normalize(model) is "ILCE-7M5" or "ILCE-7RM6";
        return new(mode==2?SensorFrame.ApsC:mode==1&&fullFrameBody?SensorFrame.FullFrame:SensorFrame.Unknown,mode);
    }
    private bool Observe(CropObservation observation) {
        bool notify=(previous is null && observation.State==SensorFrame.ApsC) ||
            (previous is not null && previous!=observation.State);
        previous=observation.State;Current=observation;return notify;
    }
    public static CropObservation VerifyRaw(CropObservation body,string connectedModel,RawInfo raw,uint format) {
        if(CameraSupport.Normalize(raw.Model)!=CameraSupport.Normalize(connectedModel))return new(SensorFrame.Unknown,body.BodyMode);
        if(body.State==SensorFrame.Unknown)return body;
        // Match the model, compression mode and reviewed active geometry together with body evidence.
        // Reduced-size lossless S/M are not crop evidence, and remain unverified.
        if(format is 5 or 6)return body;
        (uint width,uint height)? expected=(CameraSupport.Normalize(raw.Model),body.State,format) switch {
            ("ILCE-7M5",SensorFrame.FullFrame,1 or 2 or 3 or 4 or 7)=>(7028u,4688u),
            ("ILCE-7M5",SensorFrame.ApsC,1 or 3)=>(4640u,3088u),
            ("ILCE-7M5",SensorFrame.ApsC,2 or 7)=>(4624u,3080u),
            ("ILCE-7RM6",SensorFrame.ApsC,1 or 2 or 3 or 7)=>(6528u,4352u),
            _=>null
        };
        if(expected is null)return body;
        return raw.Width==expected.Value.width && raw.Height==expected.Value.height
            ? body with {RawVerified=true} : new(SensorFrame.Unknown,body.BodyMode);
    }
}
