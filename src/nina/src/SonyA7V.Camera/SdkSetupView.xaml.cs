using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SonyA7V.Camera;

public partial class SdkSetupView : UserControl {
    private readonly PluginStorage storage;
    public SdkSetupView() : this(PluginStorage.Current) { }
    public SdkSetupView(PluginStorage storage) {
        this.storage = storage; InitializeComponent();
        Status.Text = "启动时自动查找并配置 SDK；未找到时可在此选择目录。";
        if(SdkStartup.Latest is {} result)SetInitialResult(result);
    }
    public void SetInitialResult(SdkSetupResult result) {
        Status.Text=result.Message;if(result.Root is not null)SdkPath.Text=result.Root;
        Elevate.IsChecked=result.Kind==SdkSetupKind.NeedsPermission;
    }
    private async void AutoConfigure(object sender,RoutedEventArgs args) {
        ImportButton.IsEnabled=VerifyButton.IsEnabled=AutoButton.IsEnabled=false;Status.Text="正在自动查找 SDK…";
        try{SetInitialResult(await new SdkBootstrap(storage).EnsureAsync(allowElevation:true));}
        finally{ImportButton.IsEnabled=VerifyButton.IsEnabled=AutoButton.IsEnabled=true;}
    }
    private void Browse(object sender, RoutedEventArgs args) {
        var picker = new OpenFolderDialog { Title = "选择官方 SDK 的 SimpleCli 目录", Multiselect = false };
        if (picker.ShowDialog() == true) SdkPath.Text = picker.FolderName;
    }
    public void CheckConfiguration() {
        try { storage.Verify(); Status.Text = "依赖配置完整。此检查未加载 SDK，也未连接相机。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void Verify(object sender, RoutedEventArgs args) => CheckConfiguration();
    private async void Import(object sender, RoutedEventArgs args) => await ImportSdkAsync();
    public async Task ImportSdkAsync() {
        if (SonyCameraService.Shared.Connected) { Status.Text = "请先断开 Sony 相机，再导入 SDK。"; return; }
        // A disconnected SDK can still have pinned modules; restart rather than mutate its files.
        if (Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => string.Equals(m.ModuleName,"Cr_Core.dll",StringComparison.OrdinalIgnoreCase))) {
            Status.Text = "本次 NINA 已加载 Sony SDK。请重新启动后，在连接相机前导入。"; return;
        }
        ImportButton.IsEnabled = VerifyButton.IsEnabled = AutoButton.IsEnabled = false;
        try {
            await SonyCameraService.Shared.ConfigureDeploymentAsync(async () => {
            var sdk = Path.GetFullPath(SdkPath.Text.Trim()); PluginStorage.NoLinks(sdk);
            if (!Directory.Exists(sdk)) throw new DirectoryNotFoundException("请选择 SimpleCli 目录。");
            Status.Text = "正在校验并导入 SDK…";
            var provision=await SdkProvisioner.ImportAsync(storage,sdk,Elevate.IsChecked==true);
            if(provision.ExitCode!=0)throw new IOException(provision.Message);
            storage.Verify();
            Status.Text = "SDK 配置完成。请重新启动 NINA，再连接相机。";
            });
        } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { Status.Text = "已取消管理员授权；未继续配置。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally {
            ImportButton.IsEnabled = VerifyButton.IsEnabled = AutoButton.IsEnabled = true;
        }
    }
}
