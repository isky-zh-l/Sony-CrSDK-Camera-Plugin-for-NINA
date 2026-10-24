using System.Diagnostics;
using System.Text.Json;
namespace SonyCamera.Client;
/// <summary>Optional post-shot metadata helper. ExifTool is an explicitly supplied external executable, not bundled and not needed for camera control.</summary>
public static class ArwMetadata {
    public static async Task<ArwTemperature> ReadTemperatureAsync(string arwPath,string exifToolPath,CancellationToken cancellationToken=default){
        if(!Path.IsPathFullyQualified(arwPath)||!Path.IsPathFullyQualified(exifToolPath))throw new ArgumentException("请显式指定ARW和ExifTool的完整路径");
        if(!File.Exists(arwPath)||!File.Exists(exifToolPath))throw new FileNotFoundException("ARW或ExifTool不存在");
        var start=new ProcessStartInfo(exifToolPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"-j","-n","-CameraTemperature","--",arwPath})start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("无法启动ExifTool");
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);budget.CancelAfter(TimeSpan.FromSeconds(30));
        try{
            var outputTask=process.StandardOutput.ReadToEndAsync(budget.Token);var errorTask=process.StandardError.ReadToEndAsync(budget.Token);
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);var output=await outputTask.ConfigureAwait(false);var error=await errorTask.ConfigureAwait(false);
            if(process.ExitCode!=0)throw new IOException("ExifTool读取失败："+error);
            using var doc=JsonDocument.Parse(output);double? value=null;
            if(doc.RootElement.ValueKind!=JsonValueKind.Array||doc.RootElement.GetArrayLength()!=1)throw new InvalidDataException("ExifTool返回格式异常");
            if(doc.RootElement[0].TryGetProperty("CameraTemperature",out var tag)&&tag.ValueKind==JsonValueKind.Number&&tag.TryGetDouble(out double c)&&double.IsFinite(c))value=c;
            return new(arwPath,value,"ARW CameraTemperature via ExifTool (post-shot)",DateTimeOffset.UtcNow);
        }catch{if(!process.HasExited)try{process.Kill(entireProcessTree:true);}catch{}throw;}
    }
}
