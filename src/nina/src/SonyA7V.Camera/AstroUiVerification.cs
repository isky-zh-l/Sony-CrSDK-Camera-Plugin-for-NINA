using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using NINA.Plugin.Interfaces;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SonyCamera.Client;
namespace SonyA7V.Camera;
// Explicit, process-local test hooks. Opening normal plugin settings never schedules these.
internal static class AstroUiVerification {
 internal static void ScheduleIfRequested(string pluginName) {
  var root=Environment.GetEnvironmentVariable("SONY_CRSDK_UI_VERIFY");if(string.IsNullOrWhiteSpace(root))return;
  _=Task.Run(async()=>{await Task.Delay(TimeSpan.FromSeconds(20));await Application.Current.Dispatcher.InvokeAsync(async()=>{
   Window? window=null;var report=new Dictionary<string,object?>();
   var dispatcherErrors=new List<string>();
   DispatcherUnhandledExceptionEventHandler captureErrors=(_,e)=>{dispatcherErrors.Add(e.Exception.ToString());e.Handled=true;};
   Application.Current.DispatcherUnhandledException+=captureErrors;
   try{Directory.CreateDirectory(root);report["optionsResource"]=Application.Current.TryFindResource(pluginName+"_Options") is DataTemplate;
    if(!(bool)report["optionsResource"]!)throw new InvalidOperationException("NINA did not merge exported options resource");
    var template=(DataTemplate)Application.Current.FindResource(pluginName+"_Options");
    var view=(AstroSettingsView)template.LoadContent();window=new Window{Title="Sony CrSDK · NINA 界面验证",Content=view,Width=1000,Height=860};window.Show();
    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    var tabs=(TabControl)view.FindName("Sections");for(int i=0;i<3;i++){tabs.SelectedIndex=i;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Render(view,Path.Combine(root,"nina-tab-"+i+".png"));}
    var dockTemplate=Application.Current.TryFindResource(new DataTemplateKey(typeof(AstroDockViewModel))) as DataTemplate
        ??throw new InvalidOperationException("Missing dock data template");
    var loader=FindPluginLoader(Application.Current.MainWindow.DataContext)
        ??throw new InvalidOperationException("Cannot inspect actual NINA plugin loader");
    var dock=loader.DockableVMs.OfType<AstroDockViewModel>().Single();
    if(!ReferenceEquals(dock.Session,SonyCameraService.Shared))throw new InvalidOperationException("Dock is not using shared camera session");
    report["dockExportLoaded"]=true;
    if(!dock.ImageGeometry.IsFrozen)throw new InvalidOperationException("Dock icon must be frozen across composition threads");
    var imagingTab=Descendants<TabItem>(Application.Current.MainWindow).First(x=>Descendants<TextBlock>(x).Any(t=>t.Text is "Imaging" or "成像"));
    imagingTab.IsSelected=true;dock.IsClosed=false;dock.IsVisible=true;
    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    if(dispatcherErrors.Count>0)throw new InvalidOperationException(string.Join("\n",dispatcherErrors));
    report["imagingWorkspaceOpened"]=true;
    AstroSettingsView? actualDockView=null;
    for(int attempt=0;attempt<50;attempt++){
     SonyDockNavigation.TryActivate(Application.Current.MainWindow,dock.ContentId);
     await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
     actualDockView=Descendants<AstroSettingsView>(Application.Current.MainWindow).FirstOrDefault(x=>x.IsVisible);
     if(actualDockView is not null){report["dockActivationAttempts"]=attempt+1;break;}
     await Task.Delay(100);
    }
    report["actualDockVisible"]=actualDockView is not null;
    if(actualDockView is null)throw new InvalidOperationException("Sony control is not visible in actual imaging workspace");
    Render(Application.Current.MainWindow,Path.Combine(root,"nina-imaging-workspace.png"));

    var dockHost=new ContentControl{Content=dock};window.Content=dockHost;window.Height=650;
    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    var dockView=Descendants<AstroSettingsView>(dockHost).Single();
    var dockTabs=(TabControl)dockView.FindName("Sections");dockTabs.SelectedIndex=2;
    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    var scroll=Descendants<ScrollViewer>(dockView).First(x=>x.ScrollableHeight>0&&x.VerticalScrollBarVisibility==ScrollBarVisibility.Auto);
    var text=Descendants<TextBox>(scroll).First(x=>x.IsVisible);
    var before=scroll.VerticalOffset;
    var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.PreviewMouseWheelEvent};text.RaiseEvent(wheel);
    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    report["dockWheel"]=new{before,after=scroll.VerticalOffset,handled=wheel.Handled};
    if(!wheel.Handled||scroll.VerticalOffset<=before)throw new InvalidOperationException("Dock wheel did not scroll page");
    Render(dockHost,Path.Combine(root,"nina-dock.png"));
    report["sdkLoaded"]=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(x=>x.ModuleName.Equals("Cr_Core.dll",StringComparison.OrdinalIgnoreCase));
    if((bool)report["sdkLoaded"]!)throw new InvalidOperationException("Offline UI check unexpectedly loaded SDK");report["result"]="PASS";
   }catch(Exception ex){report["result"]="FAIL";report["error"]=ex.ToString();}
   finally{Application.Current.DispatcherUnhandledException-=captureErrors;window?.Close();File.WriteAllText(Path.Combine(root,"nina-ui-result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));if(Environment.GetEnvironmentVariable("SONY_CRSDK_UI_VERIFY_EXIT")=="1")Application.Current.Shutdown(report["result"] as string=="PASS"?0:1);}
  });});
 }
 private static IPluginLoader? FindPluginLoader(object root) {
  var queue=new Queue<(object Value,int Depth)>();queue.Enqueue((root,0));var seen=new HashSet<object>(ReferenceEqualityComparer.Instance);
  while(queue.Count>0){var (value,depth)=queue.Dequeue();if(!seen.Add(value))continue;if(value is IPluginLoader loader)return loader;if(depth>=3)continue;
   foreach(var field in value.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)){
    var name=field.FieldType.Name;if(!(name.Contains("ApplicationVM")||name.Contains("MainWindowVM")||name.Contains("PluginLoader")))continue;
    if(field.GetValue(value) is {} child)queue.Enqueue((child,depth+1));
   }
  }return null;
 }
 private static IEnumerable<T> Descendants<T>(DependencyObject node) where T:DependencyObject {
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);if(child is T item)yield return item;foreach(var descendant in Descendants<T>(child))yield return descendant;}
 }
 internal static void Render(FrameworkElement view,string path){view.UpdateLayout();var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var f=File.Create(path);encoder.Save(f);}
 internal static async Task<object> TestHardwareAsync(SonyA7VCamera camera,string root){
  var service=SonyCameraService.Shared;
  if(CameraSupport.Normalize(service.Model)!="ILCE-7M5")throw new InvalidOperationException("This hardware test is authorized for A7M5 only");
  var probes=await service.ReadUiPropertiesAsync();Directory.CreateDirectory(root);
  File.WriteAllText(Path.Combine(root,"hardware-properties-before.json"),JsonSerializer.Serialize(probes,new JsonSerializerOptions{WriteIndented=true}));
  var nr=probes.Single(x=>x.Property==CameraProperty.LongExposureNoiseReduction).Info;
  var restoreNr=nr?.Current;
  var baselinePath=Environment.GetEnvironmentVariable("SONY_A7V_TEST_BASELINE");
  if(!string.IsNullOrWhiteSpace(baselinePath)){
   var baseline=JsonSerializer.Deserialize<PropertyProbe[]>(File.ReadAllText(baselinePath))??throw new InvalidDataException("Missing explicit baseline");
   restoreNr=baseline.Single(x=>x.Property==CameraProperty.LongExposureNoiseReduction).Info?.Current??restoreNr;
  }
  var result=new Dictionary<string,object?>{["model"]=service.Model,["batteryVisible"]=camera.HasBattery,["batteryPercent"]=camera.BatteryLevel,["properties"]=probes.Count};
  // Exercise actual UI commands on the NINA dispatcher, preserving defaults and original NR.
  await Application.Current.Dispatcher.InvokeAsync(async()=>{
   var view=new AstroSettingsView(service,new PluginSettingsStore(Path.GetDirectoryName(typeof(AstroSettingsView).Assembly.Location)!));
   var window=new Window{Title="Sony A7M5 · 实机参数验收",Content=view,Width=1000,Height=860};window.Show();
   try{
    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);var vm=(AstroSettingsViewModel)view.DataContext;await vm.RefreshAsync();if(vm.IsError)throw new InvalidOperationException(vm.Message);
    if(nr is {Readable:true,Writable:true}&&nr.Candidates.Contains(1)&&nr.Candidates.Contains(2)){
     var row=vm.Rows.Single(x=>x.Definition.Property==CameraProperty.LongExposureNoiseReduction);row.Selected=row.Choices.Single(x=>x.Value!=(long)nr.Current);await vm.ApplyRowAsync(row);if(vm.IsError)throw new InvalidOperationException(vm.Message);
     row=vm.Rows.Single(x=>x.Definition.Property==CameraProperty.LongExposureNoiseReduction);row.Selected=row.Choices.Single(x=>x.Value==nr.Current);await vm.ApplyRowAsync(row);if(vm.IsError)throw new InvalidOperationException(vm.Message);result["uiNrRoundTrip"]="PASS";
     // Force a real preparation change even when NR was originally already off.
     row=vm.Rows.Single(x=>x.Definition.Property==CameraProperty.LongExposureNoiseReduction);
     if(row.Info!.Current!=2){row.Selected=row.Choices.Single(x=>x.Value==2);await vm.ApplyRowAsync(row);if(vm.IsError)throw new InvalidOperationException(vm.Message);}
     vm.LongNrOff=true;vm.HighIsoNrOff=false;vm.SteadyShotOff=false;vm.LosslessRaw=false;vm.ManualFocus=false;
     await vm.PrepareAsync();if(vm.IsError)throw new InvalidOperationException(vm.Message);result["receipt"]=service.PreparationReceipt?.Changes;
     await vm.RestoreAsync();if(vm.IsError)throw new InvalidOperationException(vm.Message);result["prepareRestore"]="PASS";
    }else result["uiNrRoundTrip"]="UNAVAILABLE in current camera mode";
    await vm.RefreshAsync();
    await vm.RefreshAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);AstroUiVerification.Render(view,Path.Combine(root,"hardware-parameters.png"));
   }finally{
    window.Close();
    if(service.PreparationReceipt?.Changes.Any(x=>x.Submitted)==true&&!service.Unconfirmed)await service.RestoreAstroAsync();
    if(nr is {Readable:true}&&!service.Unconfirmed){var current=(await service.ReadUiPropertiesAsync()).Single(x=>x.Property==nr.Property).Info;if(current is {Writable:true}&&restoreNr.HasValue&&current.Current!=restoreNr.Value)await service.ApplyUiPropertyAsync(service.ConnectionId,nr.Property,current.Current,restoreNr.Value);}
   }
  }).Task.Unwrap();
  if(service.Facts?.LiveViewAvailable==true){
   await service.StartLiveViewAsync();try{var frame=await service.GetLiveViewFrameAsync(CancellationToken.None);if(frame is null||frame.Jpeg.Length==0)throw new InvalidDataException("No live view JPEG");File.WriteAllBytes(Path.Combine(root,"liveview.jpg"),frame.Jpeg);result["liveView"]=new{frame.Width,frame.Height,bytes=frame.Jpeg.Length};}finally{await service.StopLiveViewAsync();}
  }
  result["longNrAfterRestore"]=(await service.ReadUiPropertiesAsync()).Single(x=>x.Property==CameraProperty.LongExposureNoiseReduction).Info?.Current;
  result["longNrRestoreTarget"]=restoreNr;
  File.WriteAllText(Path.Combine(root,"hardware-settings-result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));return result;
 }
}
