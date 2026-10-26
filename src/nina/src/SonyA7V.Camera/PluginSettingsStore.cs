using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace SonyA7V.Camera;
public sealed record PluginSettingsDocument(PluginSettings Settings,string Sha256);
public sealed class PluginSettingsStore(string directory) {
    private static readonly object gate=new();
    private string FilePath=>Path.Combine(directory,"settings.json");
    private static string Hash(string path)=>File.Exists(path)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))):"missing";
    private static readonly JsonSerializerOptions options=new(){PropertyNameCaseInsensitive=true,ReadCommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true,IgnoreReadOnlyProperties=true};
    public PluginSettingsDocument Read() {
        lock(gate) {
            var settings=File.Exists(FilePath)?JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(FilePath),options)??throw new InvalidDataException(Text.T("设置文件不能为空")):new();
            settings.ValidateCameraSettings();return new(settings,Hash(FilePath));
        }
    }
    public PluginSettingsDocument Save(PluginSettings settings,string expectedHash) {
        settings.ValidateCameraSettings();
        if(settings.SaveOriginalRaw is not("never" or "also" or "only"))throw new InvalidDataException(Text.T("请选择有效的原片保存方式"));
        if(string.IsNullOrWhiteSpace(settings.RawCopyFolder)||settings.ValidatedRawCopyFolder!=settings.RawCopyFolder)throw new InvalidDataException(Text.T("原片子目录须为单个有效文件夹名"));
        lock(gate) {
            Directory.CreateDirectory(directory);
            if(Hash(FilePath)!=expectedHash)throw new InvalidOperationException(Text.T("设置已被其他窗口或程序修改，请重新载入后再保存"));
            if(File.Exists(FilePath)&&(File.GetAttributes(FilePath)&FileAttributes.ReparsePoint)!=0)throw new IOException(Text.T("不能写入链接形式的设置文件"));
            var node=File.Exists(FilePath)?JsonNode.Parse(File.ReadAllText(FilePath),documentOptions:new(){CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true}) as JsonObject??throw new InvalidDataException(Text.T("设置应为 JSON 对象")):new JsonObject();
            Merge(node,JsonSerializer.SerializeToNode(settings,options)!.AsObject());
            var temp=Path.Combine(directory,".settings-"+Guid.NewGuid().ToString("N")+".tmp");
            try {File.WriteAllText(temp,node.ToJsonString(options));if(File.Exists(FilePath))File.Replace(temp,FilePath,FilePath+".bak");else File.Move(temp,FilePath);}
            finally {if(File.Exists(temp))File.Delete(temp);}
            return new(settings,Hash(FilePath));
        }
    }
    private static void Merge(JsonObject target,JsonObject changes) {
        foreach(var pair in changes) {
            var keys=target.Select(x=>x.Key).Where(x=>string.Equals(x,pair.Key,StringComparison.OrdinalIgnoreCase)).ToArray();
            var old=keys.Length==1?target[keys[0]]:null;
            JsonNode? next;
            if(old is JsonObject oldObject&&pair.Value is JsonObject newObject){var clone=oldObject.DeepClone().AsObject();Merge(clone,newObject);next=clone;}else next=pair.Value?.DeepClone();
            foreach(var key in keys)target.Remove(key);target[pair.Key]=next;
        }
    }
}
