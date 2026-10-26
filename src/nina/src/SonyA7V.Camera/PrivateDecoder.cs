using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace SonyA7V.Camera;

[StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
public struct RawInfo {
    public uint Abi, RawWidth, RawHeight, Pitch, Width, Height, Left, Top;
    public uint RawBits, Maximum, Black, Errors;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst=4)] public uint[] CBlack;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=5)] public string Cfa;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string Model;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Decoder;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Version;
}

public sealed class PrivateDecoder {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open(IntPtr data, ulong length, out IntPtr frame, out RawInfo info, StringBuilder error, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Copy(IntPtr frame, [Out] ushort[] pixels, ulong count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Close(IntPtr frame);
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    private readonly Open open; private readonly Copy copy; private readonly Close close;
    // Keep private modules loaded until process exit; no changes to process search paths.
    private readonly IntPtr module;
    public PrivateDecoder() {
        var root=Path.Combine(Path.GetDirectoryName(typeof(PrivateDecoder).Assembly.Location)!, "native");
        var hashes=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(root,"identity.json")))!;
        foreach(var name in new[]{"libraw.dll","SonyArw6.dll"}) {
            var actual=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,name))));
            if (!hashes.TryGetValue(name,out var expected) || actual!=expected) throw new InvalidDataException(Text.T("Decoder identity mismatch: ")+name);
        }
        foreach(System.Diagnostics.ProcessModule existing in System.Diagnostics.Process.GetCurrentProcess().Modules) {
            if(string.Equals(existing.ModuleName,"libraw.dll",StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(existing.FileName,Path.Combine(root,"libraw.dll"),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Text.T("Another libraw.dll is already loaded: ")+existing.FileName);
        }
        module=LoadLibraryEx(Path.Combine(root,"SonyArw6.dll"),IntPtr.Zero,0x100|0x1000);
        if(module==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        open=Marshal.GetDelegateForFunctionPointer<Open>(NativeLibrary.GetExport(module,"arw6_open"));
        copy=Marshal.GetDelegateForFunctionPointer<Copy>(NativeLibrary.GetExport(module,"arw6_copy"));
        close=Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(module,"arw6_close"));
    }
    public (ushort[] Pixels, RawInfo Info) Decode(byte[] bytes, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        var error=new StringBuilder(512);
        var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        IntPtr frame=IntPtr.Zero;
        try {
            var rc=open(pinned.AddrOfPinnedObject(),(ulong)bytes.LongLength,out frame,out var info,error,512);
            if(rc!=0) throw new InvalidDataException($"RAW unpack failed ({rc}): {error}");
            token.ThrowIfCancellationRequested();
            if(info.Abi!=1 || info.Errors!=0) throw new InvalidDataException(Text.T("Invalid decoder ABI or RAW error"));
            CameraGeometry.FromRaw(info); // validate before any allocation, including checked wide multiplication
            var pixels=new ushort[checked((int)((ulong)info.Width*info.Height))];
            if(copy(frame,pixels,(ulong)pixels.LongLength)!=0) throw new InvalidDataException(Text.T("RAW pixel copy failed"));
            token.ThrowIfCancellationRequested();
            return (pixels,info);
        } finally { if(frame!=IntPtr.Zero) close(frame); pinned.Free(); }
    }
}

