using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SonyCamera.Client;

namespace SonyA7V.Camera;

/// <summary>User data survives the plugin manager replacing the entire assembly directory.</summary>
public sealed class PluginStorage(string pluginDirectory, string dataDirectory, string hostDirectory) {
    public const string Identifier = "9c1f3b7e-5a24-4f0c-9a86-2d1e7c4b6a51";
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "PluginData", Identifier);
    public static string AssemblyDirectory => Path.GetDirectoryName(typeof(PluginStorage).Assembly.Location)!;
    public static PluginStorage Current => new(AssemblyDirectory, DefaultDataDirectory, AppContext.BaseDirectory);
    private static readonly object gate = new();
    public string DataDirectory { get; } = Path.GetFullPath(dataDirectory);
    public string HostDirectory { get; } = Path.GetFullPath(hostDirectory);
    public string PluginDirectory { get; } = Path.GetFullPath(pluginDirectory);
    public string SdkCacheDirectory => Path.Combine(DataDirectory,"sdk",Hash(Path.Combine(PluginDirectory,"config","sdk-runtime-lock.json")));
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public static void NoLinks(string path) {
        string? current = Path.GetFullPath(path);
        while (current is not null) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Text.T("不接受链接目录或文件：") + current);
            current = Path.GetDirectoryName(current);
        }
    }
    public string SettingsDirectory() {
        lock (gate) {
            NoLinks(DataDirectory); Directory.CreateDirectory(DataDirectory);
            var target = Path.Combine(DataDirectory, "settings.json");
            var legacy = Path.Combine(PluginDirectory, "settings.json");
            if (!File.Exists(target) && File.Exists(legacy)) { NoLinks(legacy); File.Copy(legacy, target, false); }
            NoLinks(target); return DataDirectory;
        }
    }
    public CameraDeployment LoadDeployment() {
        lock (gate) {
            var lockPath = Path.Combine(PluginDirectory, "config", "sdk-runtime-lock.json");
            NoLinks(lockPath);
            var sdkCache = Path.Combine(DataDirectory, "sdk", Hash(lockPath));
            if (!Directory.Exists(sdkCache))
                throw new InvalidOperationException(Text.T("请先在插件设置的“SDK 配置”页导入 Sony SDK 2.02.00 Windows x64，然后重新启动 NINA。"));
            var template = Path.Combine(PluginDirectory, "config", "native-manifest.template.json"); NoLinks(template);
            var bytes = File.ReadAllBytes(template);
            using var json = JsonDocument.Parse(bytes.AsMemory().Span.StartsWith(new byte[]{0xef,0xbb,0xbf}) ? bytes.AsMemory(3) : bytes.AsMemory());
            var root = json.RootElement;
            if (root.GetProperty("backend").GetString() != "production" || root.GetProperty("abiVersion").GetUInt32() != 2)
                throw new InvalidDataException(Text.T("公开包必须使用 production ABI 2。"));
            var snapshot = Path.Combine(DataDirectory, "runtime", Convert.ToHexString(SHA256.HashData(bytes)));
            NoLinks(snapshot);
            var approved = Path.Combine(snapshot, "approved-native.json");
            // Never repair an existing immutable snapshot silently; reject corruption.
            if (!Directory.Exists(snapshot)) {
                Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
                var pending = snapshot + "." + Guid.NewGuid().ToString("N") + ".tmp";
                Directory.CreateDirectory(pending);
                try {
                    foreach (var file in root.GetProperty("files").EnumerateArray()) {
                        var name = file.GetProperty("path").GetString()!;
                        var role = file.GetProperty("role").GetString();
                        var expected = file.GetProperty("sha256").GetString()!;
                        var native = file.GetProperty("base").GetString() == "native";
                        // Accept only paths checked by the unchanged native validator afterwards.
                        if (Path.IsPathRooted(name) || name.Contains('\\') || name.Split('/').Any(x => x is ".." or "." or "")) throw new InvalidDataException(Text.T("无效依赖路径"));
                        var source = !native ? Path.Combine(HostDirectory, name) : role == "wrapper" ? Path.Combine(PluginDirectory, "native", name) : Path.Combine(sdkCache, name);
                        NoLinks(source);
                        if (Hash(source) != expected.ToUpperInvariant()) throw new InvalidDataException(Text.T("依赖校验失败：") + name);
                        if (native) File.Copy(source, Path.Combine(pending, name));
                    }
                    File.WriteAllBytes(Path.Combine(pending, "approved-native.json"), bytes);
                    Directory.Move(pending, snapshot);
                } finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
            }
            NoLinks(approved);
            if (Hash(approved) != Convert.ToHexString(SHA256.HashData(bytes))) throw new InvalidDataException(Text.T("运行时清单已改变，请重新配置 SDK。"));
            var receiveRoot = Path.Combine(DataDirectory, "receive");
            var preference = Path.Combine(DataDirectory, "receive-root.txt");
            if (File.Exists(preference)) { NoLinks(preference); receiveRoot = File.ReadAllText(preference).Trim(); }
            if (!Path.IsPathFullyQualified(receiveRoot)) throw new InvalidDataException(Text.T("接收缓存必须是绝对路径。"));
            NoLinks(receiveRoot);
            return new CameraDeployment { ManifestPath = approved, ApprovedManifestSha256 = Hash(template), ReceiveRoot = receiveRoot };
        }
    }
    public void VerifyReadOnly() {
        // Startup must never create runtime snapshots or install adapters.
        var template=Path.Combine(PluginDirectory,"config","native-manifest.template.json");NoLinks(template);
        using var document=JsonDocument.Parse(File.ReadAllText(template).TrimStart('\uFEFF'));
        var root=document.RootElement;
        if(root.GetProperty("backend").GetString()!="production" || root.GetProperty("abiVersion").GetInt32()!=2)
            throw new InvalidDataException("Invalid production manifest");
        var entries=root.GetProperty("files").EnumerateArray().ToArray();
        var roles=NativePackageValidator.RequiredRoles;
        if(entries.Length!=roles.Count)throw new InvalidDataException("Expected the fixed native dependency set");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in entries){
            var role=entry.GetProperty("role").GetString()!;
            if(!roles.Contains(role)||!seen.Add(role))throw new InvalidDataException("Invalid native dependency role: "+role);
        }
        foreach(var entry in entries){
            var name=entry.GetProperty("path").GetString()!;
            if(Path.IsPathRooted(name)||name.Contains('\\')||name.Split('/').Any(x=>x is ".." or "." or ""))throw new InvalidDataException("Invalid dependency path");
            var path=entry.GetProperty("base").GetString()=="host"?Path.Combine(HostDirectory,name):
                entry.GetProperty("role").GetString()=="wrapper"?Path.Combine(PluginDirectory,"native",name):Path.Combine(SdkCacheDirectory,name);
            NoLinks(path);
            if(!string.Equals(Hash(path),entry.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Dependency verification failed: "+name);
        }
    }
    public void Verify() {
        var deployment = LoadDeployment();
        // The production validator remains authoritative for all roles, PE architecture and hashes.
        NativePackageValidator.Validate(new(deployment.ManifestPath, deployment.ApprovedManifestSha256));
    }
}
