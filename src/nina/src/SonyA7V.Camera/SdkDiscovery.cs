using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SonyA7V.Camera;

public sealed record SdkSearchResult(string? Root, bool IncompatibleSdkSeen, bool LimitReached);

/// <summary>Bounded, read-only discovery. Never loads a candidate DLL or follows a directory link.</summary>
public sealed class SdkDiscovery(PluginStorage storage) {
    private static readonly string[] required = {"Cr_Core.dll","monitor_protocol.dll","monitor_protocol_pf.dll","CrAdapter/Cr_PTP_USB.dll","CrAdapter/Cr_PTP_IP.dll","CrAdapter/libusb-1.0.dll","CrAdapter/libssh2.dll"};
    public string? CompatibleRoot(string root) {
        try {
            if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\",StringComparison.Ordinal)) return null;
            PluginStorage.NoLinks(root);
            var lockPath=Path.Combine(storage.PluginDirectory,"config","sdk-runtime-lock.json");PluginStorage.NoLinks(lockPath);
            using var json=JsonDocument.Parse(File.ReadAllText(lockPath).TrimStart('\ufeff'));
            var entries=json.RootElement.GetProperty("sdkFiles").EnumerateArray().ToArray();
            if (entries.Length!=required.Length) throw new InvalidDataException("SDK lock does not contain seven fixed files.");
            foreach(var runtime in new[]{Path.Combine(root,"external","crsdk"),Path.Combine(root,"SimpleCli","external","crsdk"),root}) {
                if (!File.Exists(Path.Combine(runtime,"Cr_Core.dll"))) continue;
                bool valid=true;
                foreach(var name in required) {
                    var matches=entries.Where(e=>e.GetProperty("path").GetString()==name).ToArray();
                    if(matches.Length!=1){valid=false;break;}
                    var path=Path.Combine(runtime,name);PluginStorage.NoLinks(path);
                    if(!File.Exists(path)||new FileInfo(path).Length>64L*1024*1024||!string.Equals(PluginStorage.Hash(path),matches[0].GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase)){valid=false;break;}
                }
                if(valid)return Path.GetFullPath(runtime); // Configure-Sdk accepts this exact runtime layout.
            }
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        return null;
    }
    public IEnumerable<string> DefaultRoots() {
        yield return storage.SdkCacheDirectory;
        // A source checkout can normalize JSON line endings, changing the lock-file fingerprint
        // without changing SDK bytes. Revalidate older caches before asking for the original SDK.
        foreach(var cached in Children(Path.Combine(storage.DataDirectory,"sdk")))yield return cached;
        var saved=Path.Combine(storage.DataDirectory,"sdk-location.txt");
        if(File.Exists(saved)){PluginStorage.NoLinks(saved);yield return File.ReadAllText(saved).Trim();}
        foreach(var name in new[]{"SONY_CRSDK_ROOT","SONY_SDK_ROOT"}) {
            var value=Environment.GetEnvironmentVariable(name);if(!string.IsNullOrWhiteSpace(value))yield return Environment.ExpandEnvironmentVariables(value);
        }
        var profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile,"Downloads");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string? downloads=null;
        try {using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");downloads=key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;}catch(System.Security.SecurityException) { }
        if(!string.IsNullOrWhiteSpace(downloads))yield return Environment.ExpandEnvironmentVariables(downloads);
        foreach(var drive in DriveInfo.GetDrives()) {
            if(drive.DriveType is not (DriveType.Fixed or DriveType.Removable)||!drive.IsReady)continue;
            foreach(var path in Children(drive.RootDirectory.FullName))if(SdkNamed(Path.GetFileName(path)))yield return path;
        }
    }
    private static bool SdkNamed(string name)=>new[]{"sony","sdk","crsdk","cameraremote","simplecli","remotecli"}.Any(s=>name.Contains(s,StringComparison.OrdinalIgnoreCase));
    private static IEnumerable<string> Children(string root) {
        try{return Directory.EnumerateDirectories(root,"*",new EnumerationOptions{IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint|FileAttributes.System|FileAttributes.Hidden}).Take(4096).ToArray();}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){return Array.Empty<string>();}
    }
    public Task<SdkSearchResult> FindAsync(IEnumerable<string>? roots=null,CancellationToken token=default,int maxDirectories=4096,TimeSpan? budget=null) => Task.Run(()=> {
        var watch=Stopwatch.StartNew();var limit=budget??TimeSpan.FromSeconds(4);
        var queue=new PriorityQueue<(string Path,int Depth,bool SdkTree),int>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool incompatible=false;int inspected=0;
        foreach(var root in roots??DefaultRoots()) {
            token.ThrowIfCancellationRequested();
            if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||root.StartsWith(@"\\",StringComparison.Ordinal))continue;
            if(watch.Elapsed>limit)return new SdkSearchResult(null,incompatible,true);
            var candidate=CompatibleRoot(root);if(candidate is not null)return new SdkSearchResult(candidate,incompatible,false);
            bool named=SdkNamed(Path.GetFileName(root));queue.Enqueue((root,0,named),named?0:100);
        }
        while(queue.TryDequeue(out var item,out _)) {
            token.ThrowIfCancellationRequested();
            if(++inspected>maxDirectories||watch.Elapsed>limit)return new SdkSearchResult(null,incompatible,true);
            if(!seen.Add(item.Path))continue;
            try{PluginStorage.NoLinks(item.Path);}catch(IOException){continue;}
            var compatible=CompatibleRoot(item.Path);if(compatible is not null)return new SdkSearchResult(compatible,incompatible,false);
            incompatible|=File.Exists(Path.Combine(item.Path,"Cr_Core.dll"))||File.Exists(Path.Combine(item.Path,"external","crsdk","Cr_Core.dll"));
            if(item.Depth>=8)continue;
            foreach(var child in Children(item.Path)) {
                bool sdkTree=item.SdkTree||SdkNamed(Path.GetFileName(child));
                if(item.Depth<2||sdkTree)queue.Enqueue((child,item.Depth+1,sdkTree),(sdkTree?0:100)+item.Depth+1);
            }
        }
        return new SdkSearchResult(null,incompatible,false);
    },token);
}
