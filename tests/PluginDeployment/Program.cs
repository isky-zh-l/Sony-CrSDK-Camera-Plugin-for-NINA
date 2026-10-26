using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using SonyA7V.Camera;
using SonyCamera.Client;

internal static class Program {
    static string host = "", package = "", sdk = "", output = "";
    static int checks, scripts;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action, string name) { try { action(); } catch (Exception) { Check(true, name); return; } throw new Exception(name); }
    [STAThread] static int Main(string[] args) {
        host = Path.GetFullPath(args[0]); package = Path.GetFullPath(args[1]); sdk = Path.GetFullPath(args[2]); output = Path.GetFullPath(args[3]);
        if (Directory.Exists(output)) throw new IOException("Use a new isolated output directory.");
        Directory.CreateDirectory(output);
        // The official validator resolves adapters beside the process executable, as does Sony SDK.
        // This is the test process folder, never the installed NINA folder.
        foreach (var name in new[]{"NINA.exe","NINA.Core.dll"}) {
            var target = Path.Combine(AppContext.BaseDirectory,name);
            if (!File.Exists(target)) File.Copy(Path.Combine(host,name),target);
        }
        AssemblyLoadContext.Default.Resolving += (_,n) => {
            var path = Path.Combine(host,n.Name+".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        return Run();
    }
    static string CopyPackage(string name) {
        var destination = Path.Combine(output,name);
        foreach(var path in Directory.GetFiles(package,"*",SearchOption.AllDirectories)) {
            var target = Path.Combine(destination,Path.GetRelativePath(package,path));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(path,target);
        }
        return destination;
    }
    static async Task<int> Script(string script, params string[] arguments) {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach(var item in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script}.Concat(arguments))start.ArgumentList.Add(item);
        using var child=Process.Start(start)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();
        File.WriteAllText(Path.Combine(output,"script-"+(++scripts)+".txt"),(await stdout)+"\n"+(await stderr));return child.ExitCode;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Run() {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;
        app.Dispatcher.BeginInvoke(new Action(async()=>{try {
            var plugin=CopyPackage("plugin with spaces");var data=Path.Combine(output,"persistent data");var storage=new PluginStorage(plugin,data,AppContext.BaseDirectory);
            Reject(()=>storage.LoadDeployment(),"unconfigured runtime gives a setup error");
            Check(!Directory.Exists(data),"checking missing SDK does not create user data");
            var view=new SdkSetupView(storage);view.CheckConfiguration();
            Check(((TextBlock)view.FindName("Status")).Text.Contains("SDK"),"first-run configuration page is usable without SDK: "+((TextBlock)view.FindName("Status")).Text);
            Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(x=>x.ModuleName.Equals("Cr_Core.dll",StringComparison.OrdinalIgnoreCase)),"opening setup does not load SDK");
            string configure=Path.Combine(plugin,"Configure-Sdk.ps1");
            var emptyHost=Path.Combine(output,"isolated validation host");Directory.CreateDirectory(emptyHost);
            foreach(var name in new[]{"NINA.exe","NINA.Core.dll"})File.Copy(Path.Combine(host,name),Path.Combine(emptyHost,name));
            string[] setup={"-SonySdkRoot",sdk,"-NinaApp",emptyHost,"-DataDirectory",data};
            Check(await Script(configure,setup.Concat(new[]{"-ValidateOnly"}).ToArray())==0,"validate-only accepts pinned SDK");
            Check(!Directory.Exists(data),"validate-only writes no user data");
            var badSdk=Path.Combine(output,"bad sdk");Directory.CreateDirectory(badSdk);
            Check(await Script(configure,"-SonySdkRoot",badSdk,"-NinaApp",AppContext.BaseDirectory,"-DataDirectory",data)!=0,"missing SDK is rejected");
            Check(!Directory.Exists(data),"failed validation leaves no cache");
            var hostAdapter=Path.Combine(emptyHost,"CrAdapter","Cr_PTP_USB.dll");Directory.CreateDirectory(Path.GetDirectoryName(hostAdapter)!);
            // Refuse pre-existing adapters in a reused test output; never overwrite unknown bytes.
            if(File.Exists(hostAdapter))throw new IOException("Use a fresh test build directory with no adapters.");
            File.WriteAllText(hostAdapter,"conflicting adapter");
            Check(await Script(configure,setup)!=0,"different shared adapter is rejected");
            Check(File.ReadAllText(hostAdapter)=="conflicting adapter"&&!Directory.Exists(data),"adapter conflict changes no existing bytes or user cache");File.Delete(hostAdapter);
            var badPackage=CopyPackage("corrupt package");File.AppendAllText(Path.Combine(badPackage,"native","sony_wrapper.dll"),"corrupt");
            Check(await Script(Path.Combine(badPackage,"Configure-Sdk.ps1"),setup)!=0,"corrupt wrapper is rejected before deployment");
            Check(!File.Exists(hostAdapter)&&!Directory.Exists(data),"corrupt package creates no host adapter or cache");
            hostAdapter=Path.Combine(AppContext.BaseDirectory,"CrAdapter","Cr_PTP_USB.dll");
            ((TextBox)view.FindName("SdkPath")).Text=sdk;
            await view.ImportSdkAsync();
            Check(((TextBlock)view.FindName("Status")).Text.Contains("SDK 配置完成"),"actual configuration UI imports SDK using a quoted hidden child process");
            storage.Verify();Check(true,"imported runtime passes unchanged production PE/hash validator");
            var deployment=storage.LoadDeployment();Check(!deployment.ManifestPath.StartsWith(plugin,StringComparison.OrdinalIgnoreCase),"runtime manifest is outside plugin replacement folder");
            var before=PluginStorage.Hash(hostAdapter);await view.ImportSdkAsync();
            Check(PluginStorage.Hash(hostAdapter)==before,"repeated SDK import is idempotent");
            var oldPlugin=Path.Combine(output,"legacy");Directory.CreateDirectory(oldPlugin);
            File.WriteAllText(Path.Combine(oldPlugin,"settings.json"),"{\"liveViewColor\":false,\"futureField\":17}");
            var receive=Path.Combine(output,"receive");File.WriteAllText(Path.Combine(oldPlugin,"sony-a7v.json"),JsonSerializer.Serialize(new{receiveRoot=receive}));
            Check(await Script(Path.Combine(plugin,"Preserve-Settings.ps1"),"-LegacyPluginDirectory",oldPlugin,"-DataDirectory",data)==0,"legacy settings migrate before plugin-manager uninstall");
            Check(!PluginSettings.Load(storage.SettingsDirectory()).LiveViewColor,"migrated user preference takes precedence over packaged defaults");
            Check(storage.LoadDeployment().ReceiveRoot==receive,"legacy receive directory is preserved");
            File.WriteAllText(Path.Combine(oldPlugin,"settings.json"),"{\"liveViewColor\":true}");
            Check(await Script(Path.Combine(plugin,"Preserve-Settings.ps1"),"-LegacyPluginDirectory",oldPlugin,"-DataDirectory",data)==0&&!PluginSettings.Load(data).LiveViewColor,"repeat migration never overwrites newer preferences");
            var store=new PluginSettingsStore(data);var doc=store.Read();store.Save(doc.Settings with{RawCopyFolder="my-raw"},doc.Sha256);
            Check(JsonNode.Parse(File.ReadAllText(Path.Combine(data,"settings.json")))!["futureField"]!.GetValue<int>()==17,"saving preferences preserves unknown legacy keys");
            var snapshot=deployment.ManifestPath;var wrapper=Path.Combine(Path.GetDirectoryName(snapshot)!,"sony_wrapper.dll");var original=File.ReadAllBytes(wrapper);
            File.AppendAllText(wrapper,"corrupt");Reject(()=>storage.Verify(),"snapshot corruption fails rather than silently replacing pinned files");File.WriteAllBytes(wrapper,original);
            var template=Path.Combine(plugin,"config","native-manifest.template.json");var templateBytes=File.ReadAllBytes(template);
            var manifest=JsonNode.Parse(File.ReadAllText(template))!;manifest["backend"]="test";File.WriteAllText(template,manifest.ToJsonString());
            Reject(()=>storage.LoadDeployment(),"test backend cannot be imported as a production runtime");File.WriteAllBytes(template,templateBytes);
            // Simulate plugin-manager deletion/extraction; no special installer hook participates.
            Directory.Move(plugin,Path.Combine(output,"old removed plugin"));
            var replacement=CopyPackage("plugin with spaces");storage=new PluginStorage(replacement,data,AppContext.BaseDirectory);storage.Verify();
            Check(!PluginSettings.Load(storage.SettingsDirectory()).LiveViewColor&&PluginSettings.Load(data).RawCopyFolder=="my-raw","manager replacement preserves preferences and SDK import");
            Check(storage.LoadDeployment().ManifestPath==snapshot,"unchanged package reuses immutable runtime snapshot");
            manifest=JsonNode.Parse(File.ReadAllText(Path.Combine(replacement,"config","native-manifest.template.json")))!;manifest["packageId"]="sony-crsdk-upgrade-fixture";File.WriteAllText(Path.Combine(replacement,"config","native-manifest.template.json"),manifest.ToJsonString());
            var upgraded=storage.LoadDeployment();storage.Verify();Check(upgraded.ManifestPath!=snapshot&&File.Exists(snapshot),"next package gets a separate snapshot while preserving old pinned files");
            Directory.Move(replacement,Path.Combine(output,"uninstalled plugin"));
            Check(File.Exists(Path.Combine(data,"settings.json"))&&File.Exists(snapshot)&&File.Exists(hostAdapter),"manager uninstall preserves user data and shared adapter");
            CopyPackage("plugin with spaces");storage.Verify();Check(true,"reinstall reuses validated SDK without the original installer");
            await SonyCameraService.Shared.ConfigureDeploymentAsync(()=>Task.FromException(new IOException("fixture"))).ContinueWith(t=>{_ =t.Exception;});
            bool ran=false;await SonyCameraService.Shared.ConfigureDeploymentAsync(()=>{ran=true;return Task.CompletedTask;});Check(ran,"setup coordinator is released after failure");
            Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(x=>x.ModuleName.Equals("Cr_Core.dll",StringComparison.OrdinalIgnoreCase)),"all deployment and UI tests finish without loading SDK");
            var manual=Path.Combine(output,"manual plugin");var manualData=Path.Combine(output,"manual data");var backup=Path.Combine(output,"manual backups");
            string[] manualArgs={"-SonySdkRoot",sdk,"-NinaApp",AppContext.BaseDirectory,"-PluginDirectory",manual,"-DataDirectory",manualData,"-BackupRoot",backup};
            Check(await Script(Path.Combine(plugin,"Install.ps1"),manualArgs.Concat(new[]{"-ValidateOnly"}).ToArray())==0&&!Directory.Exists(manual),"manual installer validation makes no deployment");
            Check(await Script(Path.Combine(plugin,"Install.ps1"),manualArgs)==0,"manual installer uses the same persistent SDK layout");
            new PluginStorage(manual,manualData,AppContext.BaseDirectory).Verify();Check(true,"manual install passes production dependency checks");
            var receipt=Directory.GetFiles(backup,"install-receipt.json",SearchOption.AllDirectories).Single();
            Check(await Script(Path.Combine(plugin,"Uninstall.ps1"),"-ReceiptPath",receipt)==0,"manual uninstall follows its receipt");
            Check(!File.Exists(Path.Combine(manual,"SonyA7V.Camera.dll"))&&File.Exists(Path.Combine(manualData,"settings.json")),"manual uninstall retains user data");
            var discovery=new SdkDiscovery(storage);
            var runtime=discovery.CompatibleRoot(sdk);
            Check(runtime is not null,"SDK sample root is normalized to a verified runtime");
            Check(discovery.CompatibleRoot(runtime!)==runtime,"runtime directory itself is accepted");
            Check(discovery.CompatibleRoot(Path.GetDirectoryName(sdk)!)==runtime,"SDK archive extraction root is accepted");
            var arbitrary=Path.Combine(output,"user chosen location","任意目录","CameraRemoteSDK","SimpleCli","external","crsdk");
            foreach(var sourceFile in Directory.GetFiles(runtime!,"*",SearchOption.AllDirectories)) {
                var target=Path.Combine(arbitrary,Path.GetRelativePath(runtime!,sourceFile));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(sourceFile,target);
            }
            var selected=await discovery.FindAsync(new[]{Path.Combine(output,"user chosen location")});
            Check(selected.Root==arbitrary,"bounded discovery finds a nested SDK with Unicode and spaces");
            var wrong=Path.Combine(output,"wrong SDK");
            foreach(var file in Directory.GetFiles(arbitrary,"*",SearchOption.AllDirectories)) {
                var target=Path.Combine(wrong,Path.GetRelativePath(arbitrary,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);
            }
            File.AppendAllText(Path.Combine(wrong,"Cr_Core.dll"),"wrong SDK version");
            Check(discovery.CompatibleRoot(wrong) is null,"discovery rejects a changed SDK without loading it");
            Check((await discovery.FindAsync(new[]{wrong})).IncompatibleSdkSeen,"incompatible SDK is distinguished from no SDK");
            Check((await discovery.FindAsync(new[]{output},budget:TimeSpan.Zero)).LimitReached,"search time budget is enforced");
            Check((await discovery.FindAsync(new[]{output},maxDirectories:0)).LimitReached,"search directory budget is enforced");
            var autoData=Path.Combine(output,"automatic setup data");var autoStorage=new PluginStorage(plugin,autoData,AppContext.BaseDirectory);
            var missing=Path.Combine(output,"empty search");Directory.CreateDirectory(missing);
            var notFound=await new SdkBootstrap(autoStorage).EnsureAsync(new[]{missing});
            Check(notFound.Kind==SdkSetupKind.NotFound&&!Directory.Exists(autoData),"missing SDK requests a folder without writing configuration");
            var auto=await new SdkBootstrap(autoStorage).EnsureAsync(new[]{arbitrary});
            Check(auto.Kind==SdkSetupKind.Ready&&auto.Imported,"automatic setup completes without manual directory input");
            Check(File.ReadAllText(Path.Combine(autoData,"sdk-location.txt"))==arbitrary,"accepted SDK location is remembered locally");
            bool importedAgain=false;
            var existing=await new SdkBootstrap(autoStorage,(_,_,_,_)=>{importedAgain=true;throw new Exception("must not import");}).EnsureAsync(new[]{missing});
            Check(existing.Kind==SdkSetupKind.Ready&&!importedAgain,"existing SDK setup is reused without searching or reimporting");
            var previousCacheStorage=new PluginStorage(plugin,Path.Combine(output,"previous cache data"),AppContext.BaseDirectory);
            var previousCache=Path.Combine(previousCacheStorage.DataDirectory,"sdk","previous-lock-fingerprint");
            foreach(var file in Directory.GetFiles(arbitrary,"*",SearchOption.AllDirectories)){
                var target=Path.Combine(previousCache,Path.GetRelativePath(arbitrary,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);
            }
            var recovered=await new SdkBootstrap(previousCacheStorage).EnsureAsync();
            Check(recovered.Kind==SdkSetupKind.Ready&&Directory.Exists(previousCacheStorage.SdkCacheDirectory),"upgrade revalidates older SDK caches without needing the original download");
            var permissionStorage=new PluginStorage(plugin,Path.Combine(output,"permission setup data"),AppContext.BaseDirectory);var permissions=new List<bool>();
            async Task<SdkProvisionResult> PermissionImport(PluginStorage s,string path,bool elevated,CancellationToken ct) {
                permissions.Add(elevated);return elevated?await SdkProvisioner.ImportAsync(s,path,false,ct):new(5,"permission fixture");
            }
            var needsPermission=await new SdkBootstrap(permissionStorage,PermissionImport).EnsureAsync(new[]{arbitrary});
            Check(needsPermission.Kind==SdkSetupKind.NeedsPermission&&permissions.SequenceEqual(new[]{false}),"permission failure is explicit when elevation is not allowed");
            permissions.Clear();
            var elevated=await new SdkBootstrap(permissionStorage,PermissionImport).EnsureAsync(new[]{arbitrary},allowElevation:true);
            Check(elevated.Kind==SdkSetupKind.Ready&&permissions.SequenceEqual(new[]{false,true}),"automatic setup requests elevation only after access denial");
            var cancelStorage=new PluginStorage(plugin,Path.Combine(output,"cancelled setup data"),AppContext.BaseDirectory);
            var cancelled=await new SdkBootstrap(cancelStorage,(_,_,elevated,_)=>Task.FromResult(elevated?new SdkProvisionResult(1223,"cancelled"):new SdkProvisionResult(5,"permission"))).EnsureAsync(new[]{arbitrary},allowElevation:true);
            Check(cancelled.Kind==SdkSetupKind.Cancelled&&!Directory.Exists(cancelStorage.DataDirectory),"cancelled Windows permission prompt leaves setup incomplete and retriable");
            var pinned=await new SdkBootstrap(cancelStorage,(_,_,_,_)=>throw new Exception("must not import"),()=>true).EnsureAsync(new[]{arbitrary});
            Check(pinned.Kind==SdkSetupKind.Failed&&pinned.Message.Contains("已加载"),"automatic setup refuses to replace SDK after a module is loaded");
            using(var cancellation=new CancellationTokenSource()){
                cancellation.Cancel();var stopped=await new SdkBootstrap(cancelStorage).EnsureAsync(new[]{arbitrary},token:cancellation.Token);
                Check(stopped.Kind==SdkSetupKind.Cancelled,"cancelled discovery is handled without crashing plugin initialization");
            }
            var fallback=new SdkSetupView(cancelStorage);fallback.SetInitialResult(needsPermission);
            Check(((TextBox)fallback.FindName("SdkPath")).Text==arbitrary&&((CheckBox)fallback.FindName("Elevate")).IsChecked==true,"fallback page preserves found SDK and explains required permission");
            Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(x=>x.ModuleName.Equals("Cr_Core.dll",StringComparison.OrdinalIgnoreCase)),"discovery and automatic bootstrap never load SDK or contact a camera");
            var assembly=typeof(PluginStorage).Assembly;
            var ninaPlugin=new SonyA7VPlugin(null!,null!,null!,null!);
            var minimum=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x=>x.Key=="MinimumApplicationVersion").Value;
            var result=new {result="PASS",assertions=checks,pluginName=ninaPlugin.Name,identifier=assembly.GetCustomAttribute<System.Runtime.InteropServices.GuidAttribute>()!.Value,version=assembly.GetName().Version!.ToString(),informationalVersion=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,minimumApplicationVersion=minimum,sdkLoaded=false};
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));exit=0;
        }catch(Exception ex){Console.WriteLine(ex);File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());}finally{app.Shutdown(exit);}}));
        app.Run();return exit;
    }
}
