using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace SonyA7V.Camera;

public sealed record SdkProvisionResult(int ExitCode,string Message);

/// <summary>Run the same reviewed import script from automatic and explicit setup.</summary>
public static class SdkProvisioner {
    public static async Task<SdkProvisionResult> ImportAsync(PluginStorage storage,string sdk,bool elevate,CancellationToken token=default) {
        token.ThrowIfCancellationRequested();PluginStorage.NoLinks(sdk);PluginStorage.NoLinks(storage.DataDirectory);
        Directory.CreateDirectory(storage.DataDirectory);
        var resultFile=Path.Combine(storage.DataDirectory,"sdk-import-"+Guid.NewGuid().ToString("N")+".txt");
        var script=Path.Combine(storage.PluginDirectory,"Configure-Sdk.ps1");PluginStorage.NoLinks(script);
        try {
            var start=new ProcessStartInfo {
                FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"),
                UseShellExecute=elevate,WindowStyle=ProcessWindowStyle.Hidden,CreateNoWindow=!elevate,
                RedirectStandardOutput=!elevate,RedirectStandardError=!elevate
            };
            if(elevate)start.Verb="runas";
            foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-SonySdkRoot",sdk,"-NinaApp",storage.HostDirectory,"-DataDirectory",storage.DataDirectory,"-ResultFile",resultFile})start.ArgumentList.Add(arg);
            using var child=Process.Start(start)??throw new IOException(Text.T("无法启动 SDK 配置。"));
            var stdout=elevate?Task.FromResult(""):child.StandardOutput.ReadToEndAsync();
            var stderr=elevate?Task.FromResult(""):child.StandardError.ReadToEndAsync();
            // Do not abandon a filesystem transaction after the child has started.
            await child.WaitForExitAsync();await stdout;await stderr;
            var message=File.Exists(resultFile)?File.ReadAllText(resultFile):child.ExitCode==0?Text.T("SDK 配置完成。"):Text.T("SDK 导入失败，请检查目录权限和 SDK 版本。");
            return new(child.ExitCode,message);
        }catch(Win32Exception ex)when(ex.NativeErrorCode==1223){return new(1223,Text.T("已取消管理员授权；可稍后在 SDK 配置页重试。"));}
        finally{if(File.Exists(resultFile)){try{File.Delete(resultFile);}catch(IOException){}}}
    }
    public static bool SdkLoaded()=>Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m=>m.ModuleName.Equals("Cr_Core.dll",StringComparison.OrdinalIgnoreCase));
}
