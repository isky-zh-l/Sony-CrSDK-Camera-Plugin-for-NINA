using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SonyCamera.Client;

internal static class NativeLoader {
    static readonly object Gate=new(); static nint wrapper,core,cookie; static string? loadedPath,loadedManifestHash;
    static readonly List<FileStream> pinnedFiles=new();
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] static extern nint LoadLibraryExW(string path,nint file,uint flags);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] static extern nint AddDllDirectory(string path);
    [DllImport("kernel32",CharSet=CharSet.Unicode)] static extern nint GetModuleHandleW(string name);
    static Win32Exception LoadError(string role,string path) {
        int code=Marshal.GetLastWin32Error();
        return new Win32Exception(code,$"加载{role}失败：{path}。Win32={code}。若为126/127，请检查包依赖及Microsoft Visual C++ x64运行库；若为193，请检查AMD64架构。请修复部署后重新启动进程。");
    }
    internal static NativePackageReport Load(NativeDeploymentOptions options) {
        if(!OperatingSystem.IsWindows()||!Environment.Is64BitProcess) throw new PlatformNotSupportedException("仅支持Windows x64进程");
        var report=NativePackageValidator.Validate(options);
        string path=report.ValidatedFiles.Single(file=>file.Role=="wrapper").FullPath;
        string sdk=report.ValidatedFiles.Single(file=>file.Role=="core").FullPath;
        string directory=Path.GetDirectoryName(path)!;
        lock(Gate) {
            if(wrapper!=0) {if(!string.Equals(path,loadedPath,StringComparison.OrdinalIgnoreCase)||report.ManifestSha256!=loadedManifestHash)throw new InvalidOperationException("DLL已固定到进程，不能切换包或真实/替身后端");return report;}
            foreach(var file in report.ValidatedFiles)if(GetModuleHandleW(Path.GetFileName(file.FullPath))!=0)throw new InvalidOperationException($"进程已加载未知原生依赖：{file.Role}，请使用独立进程启动");
            // Prevent replacement after validation, including adapters loaded later by SDK.
            // These read handles and native modules stay pinned until process exit.
            foreach(var file in report.ValidatedFiles) {
                NativePackageValidator.NoReparse(file.FullPath);
                var stream=new FileStream(file.FullPath,FileMode.Open,FileAccess.Read,FileShare.Read);
                pinnedFiles.Add(stream);NativePackageValidator.CheckPe(stream,file.FullPath);
                if(Convert.ToHexString(SHA256.HashData(stream))!=file.Sha256)throw new InvalidDataException($"验证后文件发生变化：{file.Role} {file.FullPath}");
            }
            cookie=AddDllDirectory(directory);if(cookie==0)throw LoadError("原生目录",directory);
            core=LoadLibraryExW(sdk,0,0x100|0x1000);if(core==0)throw LoadError("core",sdk);
            wrapper=LoadLibraryExW(path,0,0x100|0x1000);if(wrapper==0)throw LoadError("wrapper",path);
            loadedPath=path;loadedManifestHash=report.ManifestSha256;
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,(name,assembly,search)=>name==Native.Dll?wrapper:0);
            return report;
        }
    }
    internal static void CheckLayout(Dictionary<string,int> sizes,Dictionary<string,int> offsets) {
        void Check<T>(string name,int size,string fields) where T:struct {
            int actual=Marshal.SizeOf<T>(); sizes[name]=actual; if(actual!=size) throw new InvalidDataException($"ABI {name}大小 {actual} != {size}");
            foreach(var field in fields.Split(',')) { var parts=field.Split(':'); int value=Marshal.OffsetOf<T>(parts[0]).ToInt32(); offsets[$"{name}.{parts[0]}"]=value; if(value!=int.Parse(parts[1])) throw new InvalidDataException($"ABI {name}.{parts[0]}偏移错误：{value}"); }
        }
        Check<Native.Error>("SwError",184,"Size:0,Code:4,Sony:8,Stage:12,Id:16,Message:24");
        Check<Native.Camera>("SwCameraInfo",168,"Size:0,Index:4,Model:8,Connection:136");
        Check<Native.Parameters>("SwParameters",32,"Size:0,Iso:4,IsoKind:8,ShutterKind:12,Seconds:16,FNumber:24");
        Check<Native.Capture>("SwCaptureResult",2304,"Size:0,State:4,Id:8,Parameters:16,ActualValid:48,Raw:52,Save:56,Notifications:60,Bytes:64,Failure:72,Directory:256,Path:1280");
        Check<Native.Request>("SwExposureRequest",40,"Size:0,Mode:4,Seconds:8,ExposureTimeout:16,DownloadTimeout:24,Setup:32,Reserved:36");
        Check<Native.Capabilities>("SwExposureCapabilities",56,"Size:0,Candidate:4,Min:8,Max:16,Step:24,MaxExposure:32,MaxDownload:40,TimerReadable:48,TimerEnabled:52");
        Check<Native.Exposure>("SwExposureInfo",464,"Size:0,Mode:4,Requested:8,Hold:16,Elapsed:24,ElapsedValid:28,Phase:32,Reason:36,Attempts:40,Reserved:44,Restore:48,Reserved2:52,ReleaseError:56,RestoreError:240,Original:424,Timer:456,TimerValid:460");
    }
}
