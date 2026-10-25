using System.Runtime.InteropServices;
using System.Text;

namespace SonyCamera.Client;

internal static unsafe partial class Native {
    internal const string Dll="sony_wrapper.dll";
    [StructLayout(LayoutKind.Sequential, Pack=8)] internal struct Error { internal uint Size; internal int Code; internal uint Sony,Stage; internal ulong Id; internal fixed byte Message[160]; }
    [StructLayout(LayoutKind.Sequential, Pack=8)] internal struct Camera { internal uint Size,Index; internal fixed byte Model[128]; internal fixed byte Connection[32]; }
    [StructLayout(LayoutKind.Sequential, Pack=8)] internal struct Parameters { internal uint Size,Iso,IsoKind,ShutterKind; internal double Seconds,FNumber; }
    [StructLayout(LayoutKind.Sequential, Pack=8)] internal struct Capture { internal uint Size,State; internal ulong Id; internal Parameters Parameters; internal uint ActualValid,Raw,Save,Notifications; internal ulong Bytes; internal Error Failure; internal fixed byte Directory[1024]; internal fixed byte Path[1024]; }
    [StructLayout(LayoutKind.Sequential, Pack=8)] internal struct Request { internal uint Size,Mode; internal double Seconds,ExposureTimeout,DownloadTimeout; internal uint Setup,Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack=8)] internal struct Capabilities { internal uint Size,Candidate; internal double Min,Max,Step,MaxExposure,MaxDownload; internal uint TimerReadable,TimerEnabled; }
    [StructLayout(LayoutKind.Sequential, Pack=8)] internal struct Exposure { internal uint Size,Mode; internal double Requested,Hold; internal uint Elapsed,ElapsedValid,Phase,Reason,Attempts,Reserved,Restore,Reserved2; internal Error ReleaseError,RestoreError; internal Parameters Original; internal uint Timer,TimerValid; }
    internal static T New<T>() where T:unmanaged { T value=default; *(uint*)&value=(uint)sizeof(T); return value; }
    internal static string Utf8(byte* p,int capacity) { var span=new ReadOnlySpan<byte>(p,capacity); var n=span.IndexOf((byte)0); if(n<0) throw new InvalidDataException("原生字符串没有NUL结尾"); return new UTF8Encoding(false,true).GetString(span[..n]); }
    internal static CameraError Convert(Error e) => new((ProjectCode)e.Code,e.Sony,e.Stage,e.Id,Utf8(e.Message,160));
    internal static CameraParameters Convert(Parameters p)=>new(p.Iso,(IsoKind)p.IsoKind,(ShutterKind)p.ShutterKind,p.Seconds,p.FNumber);
    internal static ExposureDetails Convert(Exposure e)=>new((ExposureMode)e.Mode,e.Requested,e.Hold,e.ElapsedValid!=0?e.Elapsed:null,(ExposurePhase)e.Phase,e.Reason,e.Attempts,e.Restore,Convert(e.ReleaseError),Convert(e.RestoreError),e.Original.Size==32?Convert(e.Original):new(0,IsoKind.Special,ShutterKind.Unavailable,double.NaN,double.NaN),e.TimerValid!=0?e.Timer!=0:null);
    internal static CaptureSnapshot Convert(Capture c,int rc,ExposureDetails? exposure)=>new((ProjectCode)rc,(CaptureState)c.State,c.Id,c.ActualValid!=0?Convert(c.Parameters):null,c.Raw,(SaveDestination)c.Save,c.Notifications,c.Bytes,Convert(c.Failure),Utf8(c.Directory,1024),Utf8(c.Path,1024),exposure);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_initialize(ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_enumerate(out uint count,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_camera_info(uint index,ref Camera info,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_connect(uint index,uint timeout,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_wait_ready(uint timeout,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_read_parameters(ref Parameters parameters,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_supported_iso(uint* values,uint capacity,out uint count,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_supported_exposure_seconds(double* values,uint capacity,out uint count,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_set_iso(uint requested,uint timeout,out uint actual,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_set_exposure_seconds(double requested,uint timeout,out double actual,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_capture_start([MarshalAs(UnmanagedType.LPUTF8Str)]string root,uint timeout,ref Capture output,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_capture_start_ex([MarshalAs(UnmanagedType.LPUTF8Str)]string root,[MarshalAs(UnmanagedType.LPUTF8Str)]string? prefix,uint timeout,ref Capture output,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_capture_wait(ulong id,uint wait,ref Capture output,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_capture_cancel(ulong id,ref Capture output,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_capture_reconcile(ulong id,uint wait,ref Capture output,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_check_capture_ready(ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_save_destination(out uint destination,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_set_save_destination(uint requested,uint timeout,out uint actual,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_abi_info(out uint version,out uint errorSize,out uint cameraSize,out uint parametersSize,out uint captureSize,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_exposure_abi_sizes(out uint requestSize,out uint capabilitiesSize,out uint infoSize,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_exposure_capabilities(ref Capabilities output,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_capture_start_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string root,[MarshalAs(UnmanagedType.LPUTF8Str)]string? prefix,ref Request request,ref Capture output,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_capture_exposure_info(ulong id,ref Exposure output,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_capture_end_exposure(ulong id,ref Capture output,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_disconnect(uint timeout,ref Error error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_shutdown(ref Error error);
}
