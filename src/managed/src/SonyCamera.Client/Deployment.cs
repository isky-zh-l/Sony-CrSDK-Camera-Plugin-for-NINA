using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonyCamera.Client;

public sealed record NativeDeploymentOptions(string ManifestPath,string ApprovedManifestSha256,bool AllowTestPackage=false);
public sealed record ValidatedNativeFile(string Role,string FullPath,string Sha256,ushort Machine);
public sealed record NativePackageReport(string PackageId,int SchemaVersion,uint AbiVersion,string Backend,string ManifestPath,string ManifestSha256,IReadOnlyList<ValidatedNativeFile> ValidatedFiles);

/// <summary>Read-only package validation. Approval hashes pin bytes; they do not authenticate their author.</summary>
public static class NativePackageValidator {
    internal static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,WriteIndented=true};
    internal static readonly IReadOnlyDictionary<string,(string Base,string Path)> Required=new ReadOnlyDictionary<string,(string,string)>(new Dictionary<string,(string,string)>{
        ["wrapper"]=("native","sony_wrapper.dll"),["core"]=("native","Cr_Core.dll"),["monitor"]=("native","monitor_protocol.dll"),["monitorPf"]=("native","monitor_protocol_pf.dll"),
        ["usbAdapter"]=("host","CrAdapter/Cr_PTP_USB.dll"),["ipAdapter"]=("host","CrAdapter/Cr_PTP_IP.dll"),["libusb"]=("host","CrAdapter/libusb-1.0.dll"),["libssh"]=("host","CrAdapter/libssh2.dll")});
    /// <summary>The fixed native role set. Packaging, validation and the startup check share this list.</summary>
    public static IReadOnlyCollection<string> RequiredRoles{get;}=Required.Keys.ToArray();
    static readonly Dictionary<string,int> ExpectedSizes=new(){["SwError"]=184,["SwCameraInfo"]=168,["SwParameters"]=32,["SwCaptureResult"]=2304,["SwExposureRequest"]=40,["SwExposureCapabilities"]=56,["SwExposureInfo"]=464};
    sealed class Manifest {public int SchemaVersion{get;set;} public string? PackageId{get;set;} public string? Backend{get;set;} public uint AbiVersion{get;set;} public string[]? SupportedModels{get;set;} public Dictionary<string,int>? StructureSizes{get;set;} public Entry[]? Files{get;set;}}
    sealed class Entry {public string? Role{get;set;} public string? Base{get;set;} public string? Path{get;set;} public string? Sha256{get;set;}}
    internal static string RequireHash(string? hash,string name){if(hash==null||hash.Length!=64||hash.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException(Text.F($"{name}必须是64位SHA-256十六进制文本，必须从包以外的批准配置提供"));return hash.ToUpperInvariant();}
    internal static ReadOnlyMemory<byte> JsonBytes(byte[] bytes)=>bytes.Length>=3&&bytes[0]==0xef&&bytes[1]==0xbb&&bytes[2]==0xbf?bytes.AsMemory(3):bytes.AsMemory();
    internal static void NoReparse(string path){var current=Path.GetFullPath(path);while(!string.IsNullOrEmpty(current)){if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException(Text.F($"拒绝重解析点/符号链接路径：{current}"));var parent=Path.GetDirectoryName(current);if(parent==current)break;current=parent!;}}
    internal static void NoDuplicateProperties(JsonElement element){if(element.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var item in element.EnumerateObject()){if(!names.Add(item.Name))throw new InvalidDataException(Text.F($"JSON重复属性：{item.Name}"));NoDuplicateProperties(item.Value);}}else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())NoDuplicateProperties(item);}
    internal static ushort CheckPe(Stream stream,string path){
        using var reader=new BinaryReader(stream,System.Text.Encoding.UTF8,true);
        if(stream.Length<64||reader.ReadUInt16()!=0x5A4D)throw new InvalidDataException(Text.F($"不是原生PE文件：{path}"));
        stream.Position=0x3c;int offset=reader.ReadInt32();if(offset<64||offset>stream.Length-26)throw new InvalidDataException(Text.F($"PE头越界：{path}"));stream.Position=offset;
        if(reader.ReadUInt32()!=0x4550)throw new InvalidDataException(Text.F($"PE签名无效：{path}"));ushort machine=reader.ReadUInt16();if(machine!=0x8664)throw new InvalidDataException(Text.F($"原生文件必须为AMD64，实际machine=0x{machine:X4}：{path}"));
        stream.Position=offset+20;ushort optionalSize=reader.ReadUInt16();ushort characteristics=reader.ReadUInt16();if(optionalSize<112||offset+24L+optionalSize>stream.Length||(characteristics&0x2000)==0)throw new InvalidDataException(Text.F($"需要有效原生DLL PE头：{path}"));
        stream.Position=offset+24;if(reader.ReadUInt16()!=0x20b)throw new InvalidDataException(Text.F($"需要64位PE32+：{path}"));
        // CLR data directory must be absent; these roles contain native SDK code only.
        stream.Position=offset+24+108;uint directories=reader.ReadUInt32();if(directories>14&&optionalSize>=112+15*8){stream.Position=offset+24+112+14*8;if(reader.ReadUInt64()!=0)throw new InvalidDataException(Text.F($"原生依赖不能是托管程序集：{path}"));}
        stream.Position=0;return machine;
    }
    public static NativePackageReport Validate(NativeDeploymentOptions options){
        ArgumentNullException.ThrowIfNull(options);string approved=RequireHash(options.ApprovedManifestSha256,nameof(options.ApprovedManifestSha256));
        if(!OperatingSystem.IsWindows()||!Environment.Is64BitProcess)throw new PlatformNotSupportedException(Text.T("本部署验证器仅支持Windows x64进程；不会加载DLL或访问相机"));
        if(!Path.IsPathFullyQualified(options.ManifestPath))throw new ArgumentException(Text.T("ManifestPath必须为绝对路径；配置文件中的相对路径请通过CameraClientConfiguration.Load解析"));
        string manifestPath=Path.GetFullPath(options.ManifestPath);NoReparse(manifestPath);
        if(new FileInfo(manifestPath).Length>1024*1024)throw new InvalidDataException(Text.T("部署清单不能超过1MiB"));
        byte[] bytes=File.ReadAllBytes(manifestPath);string hash=Convert.ToHexString(SHA256.HashData(bytes));if(hash!=approved)throw new InvalidDataException(Text.F($"清单未获批准：实际SHA256={hash}，与宿主外部批准值不一致"));
        var jsonBytes=JsonBytes(bytes);using var document=JsonDocument.Parse(jsonBytes);NoDuplicateProperties(document.RootElement);
        var manifest=JsonSerializer.Deserialize<Manifest>(jsonBytes.Span,JsonOptions)??throw new InvalidDataException(Text.T("清单为空"));
        if(manifest.SchemaVersion!=1||manifest.AbiVersion!=2)throw new InvalidDataException(Text.T("仅支持schemaVersion=1、abiVersion=2"));
        if(string.IsNullOrWhiteSpace(manifest.PackageId)||manifest.PackageId.Length>128)throw new InvalidDataException(Text.T("packageId必须为1～128个非空白字符"));
        if(manifest.Backend is not ("production" or "test"))throw new InvalidDataException(Text.T("backend必须为production或test"));
        if(manifest.Backend=="test"&&!options.AllowTestPackage)throw new InvalidDataException(Text.T("拒绝测试包：必须显式AllowTestPackage=true，测试包不提供实机证据"));
        // This is an explicit package model inventory, not a hardware-verification claim.
        // Keep the original one-model manifests valid while allowing reviewed multi-model packages.
        if(manifest.SupportedModels is not { Length: >0 and <=256 } ||
           manifest.SupportedModels.Any(model=>string.IsNullOrWhiteSpace(model)||model.Length>128||model.Any(char.IsControl)) ||
           manifest.SupportedModels.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=manifest.SupportedModels.Length)
            throw new InvalidDataException("supportedModels must be a nonempty, unique model inventory (at most 256 entries)");
        if(manifest.StructureSizes==null||manifest.StructureSizes.Count!=ExpectedSizes.Count||ExpectedSizes.Any(pair=>!manifest.StructureSizes.TryGetValue(pair.Key,out var size)||size!=pair.Value))throw new InvalidDataException(Text.T("清单ABI结构大小必须与客户端已知ABI2布局完全一致"));
        if(manifest.Files==null||manifest.Files.Length!=Required.Count)throw new InvalidDataException(Text.T("清单必须含8项固定原生角色，不得删除必需项或添加未审查角色"));
        var roles=new HashSet<string>(StringComparer.Ordinal);var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var files=new List<ValidatedNativeFile>();
        foreach(var entry in manifest.Files){
            if(entry?.Role==null||!roles.Add(entry.Role)||!Required.TryGetValue(entry.Role,out var required))throw new InvalidDataException(Text.T("清单角色未知、空或重复"));
            if(entry.Base!=required.Base||entry.Path!=required.Path)throw new InvalidDataException(Text.F($"角色{entry.Role}必须固定为{required.Base}:{required.Path}，不得使用绝对路径、跳转或其他路径"));
            var root=entry.Base=="native"?Path.GetDirectoryName(manifestPath)!:AppContext.BaseDirectory;
            var path=Path.GetFullPath(Path.Combine(root,entry.Path.Replace('/',Path.DirectorySeparatorChar)));
            if(!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!paths.Add(path))throw new InvalidDataException(Text.T("清单文件路径逃逸或重复"));
            try {
                NoReparse(path);string expected=RequireHash(entry.Sha256,$"{entry.Role}.sha256");
                using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);ushort machine=CheckPe(stream,path);string actual=Convert.ToHexString(SHA256.HashData(stream));if(actual!=expected)throw new InvalidDataException(Text.F($"原生文件哈希不匹配：role={entry.Role}, path={path}, actual={actual}"));
                files.Add(new(entry.Role,path,actual,machine));
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){throw new IOException(Text.F($"无法验证必需依赖：role={entry.Role}, path={path}；{ex.Message}。请检查完整部署和读取权限；host适配器须位于EXE同级CrAdapter。"),ex);}
        }
        return new(manifest.PackageId,manifest.SchemaVersion,manifest.AbiVersion,manifest.Backend,manifestPath,hash,files.AsReadOnly());
    }
}

public sealed record CameraClientConfiguration(string ManifestPath,string ApprovedManifestSha256,bool AllowTestPackage=false){
    public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SonyNinaCrSdk","client.json");
    public NativeDeploymentOptions ToDeploymentOptions()=>new(ManifestPath,ApprovedManifestSha256,AllowTestPackage);
    public static CameraClientConfiguration Load(string? configPath=null){
        string path=Path.GetFullPath(configPath??DefaultPath);byte[] bytes=File.ReadAllBytes(path);var jsonBytes=NativePackageValidator.JsonBytes(bytes);using var json=JsonDocument.Parse(jsonBytes);NativePackageValidator.NoDuplicateProperties(json.RootElement);
        var config=JsonSerializer.Deserialize<CameraClientConfiguration>(jsonBytes.Span,NativePackageValidator.JsonOptions)??throw new InvalidDataException(Text.T("配置为空"));
        NativePackageValidator.RequireHash(config.ApprovedManifestSha256,nameof(ApprovedManifestSha256));if(string.IsNullOrWhiteSpace(config.ManifestPath))throw new InvalidDataException(Text.T("配置缺少ManifestPath"));
        return config with {ManifestPath=Path.GetFullPath(config.ManifestPath,Path.GetDirectoryName(path)!)};
    }
    public void Save(string? configPath=null){
        NativePackageValidator.RequireHash(ApprovedManifestSha256,nameof(ApprovedManifestSha256));if(string.IsNullOrWhiteSpace(ManifestPath))throw new InvalidDataException(Text.T("ManifestPath不能为空"));
        string path=Path.GetFullPath(configPath??DefaultPath);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(this,NativePackageValidator.JsonOptions));
    }
}
