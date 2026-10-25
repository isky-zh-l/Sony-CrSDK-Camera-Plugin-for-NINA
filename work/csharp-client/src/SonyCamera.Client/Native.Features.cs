using System.Runtime.InteropServices;
namespace SonyCamera.Client;
internal static unsafe partial class Native {
    [StructLayout(LayoutKind.Sequential,Pack=8)] internal struct Property {internal uint Size,Id,Readable,Writable;internal long Current;internal uint Count,Range;internal long Min,Max,Step;}
    [StructLayout(LayoutKind.Sequential,Pack=8)] internal struct Frame {internal uint Size,Bytes,Width,Height,Number,TimeCode;}
    [StructLayout(LayoutKind.Sequential,Pack=8)] internal struct Folder {internal uint Size,Handle;internal ulong Session;internal fixed byte Name[256];}
    [StructLayout(LayoutKind.Sequential,Pack=8)] internal struct Content {internal uint Size,Handle;internal ulong Session,Bytes;internal uint Folder,Width,Height,Reserved;internal fixed byte Name[512];internal fixed byte Date[32];}
    [StructLayout(LayoutKind.Sequential,Pack=8)] internal struct Transfer {internal uint Size,State;internal ulong Id,Expected,Verified;internal Error Failure;internal fixed byte Path[1024];}
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_feature_abi(out uint version,out uint ps,out uint fs,out uint folders,out uint contents,out uint transfer,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_connect_mode(uint index,uint mode,uint timeout,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_property(uint id,ref Property p,long* values,uint capacity,out uint count,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_set_property(uint id,long requested,uint timeout,out long actual,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_live_view_set(uint enabled,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_live_view_read(byte* buffer,uint capacity,ref Frame p,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_movie_set(uint recording,uint timeout,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_astro_target(uint setting,out uint property,out long value,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_focus_pulse(int speed,uint hold,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_folders(Folder* folders,uint capacity,out uint count,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_get_contents(ulong session,uint folder,Content* contents,uint capacity,out uint count,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_content_start(ulong session,uint handle,[MarshalAs(UnmanagedType.LPUTF8Str)] string root,uint timeout,ref Transfer p,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_content_poll(ulong id,ref Transfer p,ref Error e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)] internal static extern int sw_content_cancel(ulong id,ref Transfer p,ref Error e);
    internal static ContentTransfer Convert(Transfer p)=>new(p.Id,(ContentTransferState)p.State,p.Expected,p.Verified,Utf8(p.Path,1024),Convert(p.Failure));
    internal static CameraFolder Convert(Folder p)=>new(p.Handle,p.Session,Utf8(p.Name,256));
    internal static CameraContent Convert(Content p)=>new(p.Handle,p.Session,p.Folder,Utf8(p.Name,512),p.Bytes,p.Width,p.Height,Utf8(p.Date,32));
}
