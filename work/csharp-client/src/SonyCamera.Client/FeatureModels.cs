namespace SonyCamera.Client;
/// <summary>Stable project IDs; option values are camera-provided tokens, not SDK property IDs or physical units.</summary>
public enum CameraProperty {
    Aperture=1,
    FocusMode=2,
    ExposureMode=3,
    DriveMode=4,
    FileFormat=5,
    RawCompression=6,
    ImageSize=7,
    AspectRatio=8,
    LongExposureNoiseReduction=9,
    HighIsoNoiseReduction=10,
    SilentMode=11,
    ShutterType=12,
    LiveViewQuality=13,
    LiveViewDisplayEffect=14,
    LiveViewStatus=15,
    BatteryPercent=16,
    BatteryLevel=17,
    RecordingState=18,
    Slot1Status=19,
    Slot2Status=20,
    Slot1RemainingImages=21,
    Slot2RemainingImages=22,
    Slot1RemainingSeconds=23,
    Slot2RemainingSeconds=24,
    FocusPosition=25,
    FocusPositionActual=26,
    FocusMagnifier=27,
    FocusMagnificationTime=28,
    InitialFocusMagnifier=29,
    MovieFormat=30,
    MovieRecordingSetting=31,
    MovieFrameRate=32,
    WhiteBalance=33,
    ColorTemperature=34,
    ExposureCompensation=35,
    MeteringMode=36,
    FocusArea=37,
    SteadyShot=38,
    AutoPowerOffTemperature=39,
    ContentsTransferStatus=40,
    ContentsTransferProgress=41,
    RemoteSaveImageSize=42,
    FocusIndicator=43,
    ApsCMode=44,
    MovieRecordingMedia=45,
}
public enum CameraConnectionMode { Remote=0, Contents=1 }
public sealed record CameraPropertyInfo(CameraProperty Property,bool Readable,bool Writable,long Current,
    IReadOnlyList<long> Candidates,bool IsRange,long Minimum,long Maximum,long Step) {
    public bool Accepts(long value)=>Writable&&(IsRange?value>=Minimum&&value<=Maximum&&Step>0&&unchecked((ulong)value-(ulong)Minimum)%(ulong)Step==0:Candidates.Contains(value));
}
public sealed record PropertyProbe(CameraProperty Property,CameraPropertyInfo? Info,CameraError? Error) {
    public bool Available=>Info!=null;
}
/// <summary>JPEG preview, NOT full sensor RAW or original recorded movie. TimeCode is optional SDK frame metadata.</summary>
public sealed record LiveViewFrame(byte[] Jpeg,uint Width,uint Height,uint FrameNumber,uint TimeCode,DateTimeOffset ReceivedAt);
public sealed record CameraFolder(uint Handle,ulong Session,string Name);
public sealed record CameraContent(uint Handle,ulong Session,uint Folder,string Name,ulong Bytes,uint Width,uint Height,string CameraDate);
public enum ContentTransferState { None=0, Waiting=1, Succeeded=2, Failed=3 }
public sealed record ContentTransfer(ulong Id,ContentTransferState State,ulong ExpectedBytes,ulong VerifiedBytes,string Path,CameraError Failure) {
    public bool IsSuccess=>State==ContentTransferState.Succeeded;
}
/// <summary>Post-shot metadata only. Null means missing/unsupported; never substitute ambient or overheat threshold.</summary>
public sealed record ArwTemperature(string SourceFile,double? Celsius,string Source,DateTimeOffset ReadAt);

public sealed record CameraBattery(int? Percent,long? RawLevel,bool PercentReadable);
