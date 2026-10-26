namespace SonyCamera.Client;

public enum ProjectCode { Ok=0, Argument=1, State=2, Sdk=3, Timeout=4, Unsupported=5, Internal=6, Buffer=7, Pending=8, Cancelled=9, Io=10, Association=11, EndedEarly=12 }
public enum CaptureState { None=0, Waiting=1, Succeeded=2, Failed=3, CancelledUnconfirmed=4, TimedOutUnconfirmed=5, EndedEarlyUnconfirmed=6 }
public enum ExposureMode { Ordinary=0, Bulb=1 }
public enum ExposurePhase { Idle=0, Holding=1, Downloading=2, Restoring=3, Terminal=4 }
public enum SaveDestination { Computer=1, ComputerAndCard=2, Card=3 }
public enum IsoKind { Fixed=0, Auto=1, Special=2 }
public enum ShutterKind { Seconds=0, Bulb=1, Unavailable=2 }
public enum SessionState { New, Initialized, Connected, Capturing, Unconfirmed, Disconnected, Closed, Faulted }
public sealed record CameraDescription(uint Index, string Model, string Connection);
public sealed record CameraParameters(uint Iso, IsoKind IsoKind, ShutterKind ShutterKind, double ExposureSeconds, double FNumber);
public sealed record CameraError(ProjectCode Code, uint SonyCode, uint Stage, ulong CaptureId, string Message);
public sealed class CameraException : Exception {
    public CameraError Error { get; }
    public CameraException(CameraError error) : base(Text.CameraError(error)) => Error=error;
}
public sealed record ExposureCapabilities(bool BulbAvailable, double BulbMinimumSeconds, double BulbMaximumSeconds, double BulbStepSeconds, double MaximumExposureTimeoutSeconds, double MaximumDownloadTimeoutSeconds, bool TimerReadable, bool TimerEnabled);
public sealed record ExposureRequest(ExposureMode Mode, double Seconds, double ExposureTimeoutSeconds, double DownloadTimeoutSeconds=120, uint SetupTimeoutMilliseconds=15000, uint ExpectedIso=0, bool RequireFullFrame=false);
public sealed record ExposureDetails(ExposureMode Mode, double RequestedSeconds, double SoftwareHoldSeconds, uint? CameraElapsedSeconds, ExposurePhase Phase, uint EndReason, uint ReleaseAttempts, uint RestoreState, CameraError ReleaseError, CameraError RestoreError, CameraParameters OriginalParameters, bool? OriginalTimerEnabled);
public sealed record CaptureSnapshot(ProjectCode Code, CaptureState State, ulong CaptureId, CameraParameters? ActualParameters, uint RawFormat, SaveDestination SaveDestination, uint DownloadNotifications, ulong FileBytes, CameraError Failure, string ReceiveDirectory, string ArwPath, ExposureDetails? Exposure) {
    public bool IsSuccess => Code==ProjectCode.Ok && State==CaptureState.Succeeded && !string.IsNullOrEmpty(ArwPath);
    public bool IsPending => Code==ProjectCode.Pending || State==CaptureState.Waiting;
}
public sealed record RecoveryResult(bool Recovered, CaptureSnapshot Capture, CameraError Error);
public sealed record ClientLog(DateTimeOffset Time, int OwnerThreadId, string Operation, string Message);
public sealed record AbiReport(uint Version, string WrapperSha256, string WrapperPath, IReadOnlyDictionary<string,int> Sizes, IReadOnlyDictionary<string,int> Offsets);
