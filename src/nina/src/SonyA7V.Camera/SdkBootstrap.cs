using System.IO;

namespace SonyA7V.Camera;

public enum SdkSetupKind { Ready, NotFound, NeedsPermission, Cancelled, Failed }
public sealed record SdkSetupResult(SdkSetupKind Kind,string Message,string? Root=null,bool Imported=false);

public sealed class SdkBootstrap(PluginStorage storage,
    Func<PluginStorage,string,bool,CancellationToken,Task<SdkProvisionResult>>? import=null,
    Func<bool>? sdkLoaded=null) {
    public async Task<SdkSetupResult> EnsureAsync(IEnumerable<string>? searchRoots=null,bool allowElevation=false,CancellationToken token=default) {
        try {
            try {await Task.Run(storage.Verify,token);return new(SdkSetupKind.Ready,"SDK 已配置，可连接相机。");}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            var found=await new SdkDiscovery(storage).FindAsync(searchRoots,token);
            if(found.Root is null)return new(SdkSetupKind.NotFound,found.IncompatibleSdkSeen?"未找到匹配的 SDK 2.02.00 Windows x64，请选择正确版本的解压目录。":"未找到 Sony SDK。请自行下载并解压官方 SDK，再选择目录。");
            if((sdkLoaded??SdkProvisioner.SdkLoaded)())return new(SdkSetupKind.Failed,"当前进程已加载 Sony SDK，请重启后再配置。",found.Root);
            SdkProvisionResult provision=new(-1,"");
            await SonyCameraService.Shared.ConfigureDeploymentAsync(async()=> {
                provision=await (import??SdkProvisioner.ImportAsync)(storage,found.Root,false,token);
                if(provision.ExitCode==5&&allowElevation)provision=await (import??SdkProvisioner.ImportAsync)(storage,found.Root,true,token);
                if(provision.ExitCode==0)await Task.Run(storage.Verify,token);
            });
            return provision.ExitCode switch {
                0=>new(SdkSetupKind.Ready,"已自动找到并配置 SDK，可连接相机。",found.Root,true),
                5=>new(SdkSetupKind.NeedsPermission,"已找到 SDK；写入 NINA 共享适配器需要管理员权限。",found.Root),
                1223=>new(SdkSetupKind.Cancelled,provision.Message,found.Root),
                _=>new(SdkSetupKind.Failed,provision.Message,found.Root)
            };
        }catch(OperationCanceledException){return new(SdkSetupKind.Cancelled,"已取消 SDK 查找。");}
        catch(Exception ex){return new(SdkSetupKind.Failed,ex.Message);}
    }
}

/// <summary>One startup task, shared with connect. Setup never calls a camera API.</summary>
public static class SdkStartup {
    private static readonly object gate=new();
    private static Task<SdkSetupResult>? startup;
    public static SdkSetupResult? Latest {get;private set;}
    public static void Start() {
        lock(gate)startup??=Task.Run(async()=> {
            var result=await new SdkBootstrap(PluginStorage.Current).EnsureAsync(allowElevation:true);
            Latest=result;
            if(result.Kind!=SdkSetupKind.Ready)SdkSetupPrompt.Schedule(result);
            return result;
        });
    }
    public static Task WaitAsync() {lock(gate)return startup??Task.CompletedTask;}
}
