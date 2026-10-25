using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using SonyA7V.Camera;
using SonyCamera.Client;
internal static class Program {
 static int checks;static string output="";
 static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
 static void Reject<T>(Action action,string name)where T:Exception{try{action();}catch(T){Check(true,name);return;}throw new Exception(name);}
 [STAThread]static int Main(string[] args){
  var host=Path.GetFullPath(args[0]);output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
  AssemblyLoadContext.Default.Resolving+=(_,n)=>{var p=Path.Combine(host,n.Name+".dll");return File.Exists(p)?AssemblyLoadContext.Default.LoadFromAssemblyPath(p):null;};
  return Run();
 }
 [MethodImpl(MethodImplOptions.NoInlining)]static int Run(){
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
  var errors=new BindingErrors();PresentationTraceSources.DataBindingSource.Listeners.Add(errors);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
  int exit=1;
  app.Dispatcher.BeginInvoke(new Action(async()=>{Window? window=null;try{
   var path=Path.Combine(output,"settings-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
   var store=new PluginSettingsStore(path);var doc=store.Read();Check(doc.Sha256=="missing","absent settings safe defaults");
   doc=store.Save(doc.Settings with{LiveViewColor=false},doc.Sha256);Check(File.Exists(Path.Combine(path,"settings.json")),"first settings save");
   File.WriteAllText(Path.Combine(path,"settings.json"),"{\"SAVEORIGINALRAW\":\"never\",\"futureField\":{\"x\":9},\"astroPreparationSelection\":{\"futurePlan\":true}}");
   doc=store.Read();doc=store.Save(doc.Settings with{RawCopyFolder="original"},doc.Sha256);
   var json=JsonNode.Parse(File.ReadAllText(Path.Combine(path,"settings.json")))!;
   Check(json["futureField"]!["x"]!.GetValue<int>()==9&&json["astroPreparationSelection"]!["futurePlan"]!.GetValue<bool>(),"preserve nested unknown settings");
   Check(json.AsObject().Count(x=>x.Key.Equals("saveOriginalRaw",StringComparison.OrdinalIgnoreCase))==1,"canonical settings key no duplicates");
   Check(File.Exists(Path.Combine(path,"settings.json.bak")),"atomic save backs up settings");
   File.AppendAllText(Path.Combine(path,"settings.json")," ");Reject<InvalidOperationException>(()=>store.Save(doc.Settings,doc.Sha256),"concurrent settings edit refused");
   doc=store.Read();Reject<InvalidDataException>(()=>store.Save(doc.Settings with{SensorWidth=5},doc.Sha256),"partial geometry refused");
   Reject<InvalidDataException>(()=>store.Save(doc.Settings with{PixelSizeMicrons=double.NaN},doc.Sha256),"NaN pitch refused");
   Reject<InvalidDataException>(()=>store.Save(doc.Settings with{RawCopyFolder=".."},doc.Sha256),"unsafe copy folder refused");
   var fake=new FakeSession();var view=new AstroSettingsView(fake,store);window=new Window{Content=view,Width=1000,Height=860,WindowStartupLocation=WindowStartupLocation.CenterScreen};window.Show();await Idle();
   var vm=(AstroSettingsViewModel)view.DataContext;Check(fake.Reads==0&&fake.Writes==0,"opening UI never reads SDK or writes camera");
   Check(!vm.RefreshCommand.CanExecute(null),"disconnected refresh disabled");Check(!vm.RecheckCommand.CanExecute(null),"disconnected recheck disabled");Save(view,"disconnected.png");
   fake.Connect();await vm.RefreshAsync();Check(vm.Rows.Count==AstroUiCatalog.Definitions.Count,"all parameter rows appear");
   fake.SetCorrection("请切换 MF，连接保留");Check(vm.CorrectionHint.Contains("MF")&&vm.RecheckCommand.CanExecute(null),"correction hint visible and recheck available while connected");
   await Idle();Save(view,"correction-hint.png");
   await vm.RecheckAsync();Check(fake.Rechecks==1&&vm.CorrectionHint.Length==0&&!vm.IsError,"recheck clears corrected hint without reconnect");
   var row=vm.Rows.Single(x=>x.Definition.Property==CameraProperty.LongExposureNoiseReduction);Check(row.Current=="开启"&&row.Choices.Count==2,"candidate labels from camera");
   Check(vm.Rows.Single(x=>x.Definition.Property==CameraProperty.Slot1RemainingImages).CanSelect==false,"read-only card counter disabled");
   Check(vm.Rows.Single(x=>x.Definition.Property==CameraProperty.Aperture).CanSelect==false,"lensless aperture disabled");
   Check(vm.Rows.Single(x=>x.Definition.Property==CameraProperty.SilentMode).Choices.Count==0,"unknown enum never invented");
   Check(vm.Rows.All(x=>x.Definition.Property!=CameraProperty.FocusMagnifier),"camera magnifier removed from UI");
   row.Selected=row.Choices.First(x=>x.Value==1);Check(vm.ApplyRowCommand.CanExecute(row),"exact changed candidate enabled");await vm.ApplyRowAsync(row);
   Check(fake.Writes==1&&!vm.IsError&&fake.Nr==1,"apply confirms and refreshes");
   fake.BlockRead=new(TaskCreationOptions.RunContinuationsAsynchronously);var pending=vm.RefreshAsync();Check(vm.Busy&&!vm.ApplyRowCommand.CanExecute(row),"pending read locks commands");fake.BlockRead.SetResult();await pending;fake.BlockRead=null;
   row=vm.Rows.Single(x=>x.Definition.Property==CameraProperty.LongExposureNoiseReduction);row.Selected=row.Choices.First(x=>x.Value==2);fake.Nr=2;await vm.ApplyRowAsync(row);Check(vm.IsError&&fake.Writes==1,"stale current value never writes");
   var stale=row;fake.Connect();Check(vm.Rows.Count==0&&!vm.ApplyRowCommand.CanExecute(stale),"connection change clears stale rows");await vm.ApplyRowAsync(stale);Check(vm.IsError&&fake.Writes==1,"direct stale row call refused");
   await vm.RefreshAsync();fake.SetBusy(true);Check(!vm.CanEdit&&!vm.PrepareCommand.CanExecute(null),"camera busy disables edits");fake.SetBusy(false);
   int before=fake.Writes;vm.AutoPrepare=false;vm.LongNrOff=true;await vm.SaveAsync();Check(!vm.IsError&&fake.Writes==before&&store.Read().Settings.AstroPreparation is null,"saving defaults does not prepare camera");
   vm.AutoPrepare=true;await vm.SaveAsync();Check(store.Read().Settings.AstroPreparation?.DisableLongExposureNoiseReduction==true,"explicit auto preparation persisted separately");
   await vm.PrepareAsync();Check(!vm.IsError&&vm.CanRestore,"preparation receipt enables same-session restore");await vm.RestoreAsync();Check(!vm.IsError&&!vm.CanRestore,"restore clears receipt");
   var tabs=(TabControl)view.FindName("Sections");
   window.Height=620;await Idle();
   var page=Descendants<ScrollViewer>(tabs).First(x=>x.ScrollableHeight>0&&x.VerticalScrollBarVisibility==ScrollBarVisibility.Auto);
   var selector=Descendants<ComboBox>(page).First(x=>x.Items.Count>0&&x.IsEnabled);
   var selected=selector.SelectedItem;
   var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.PreviewMouseWheelEvent};selector.RaiseEvent(wheel);await Idle();
   Check(wheel.Handled&&page.VerticalOffset>0&&Equals(selector.SelectedItem,selected),"wheel over closed selector scrolls page without changing parameter");
   var previous=page.VerticalOffset;var fine=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-15){RoutedEvent=Mouse.PreviewMouseWheelEvent};selector.RaiseEvent(fine);await Idle();
   Check(fine.Handled&&page.VerticalOffset>previous,"high-resolution partial wheel delta is retained");
   page.ScrollToVerticalOffset(page.ScrollableHeight);await Idle();var boundary=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.PreviewMouseWheelEvent};selector.RaiseEvent(boundary);await Idle();
   Check(boundary.Handled&&Equals(selector.SelectedItem,selected),"page boundary does not change closed camera selector");
   selector.IsDropDownOpen=true;await Idle();var popupWheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.PreviewMouseWheelEvent};selector.RaiseEvent(popupWheel);
   Check(!popupWheel.Handled,"open selector keeps its normal wheel handling");selector.IsDropDownOpen=false;
   // TextBox has its own ScrollViewer; it must not swallow the defaults page wheel.
   tabs.SelectedIndex=2;await Idle();var defaults=Descendants<ScrollViewer>(tabs).First(x=>x.ScrollableHeight>0&&x.VerticalScrollBarVisibility==ScrollBarVisibility.Auto);var text=Descendants<TextBox>(defaults).First(x=>x.IsVisible);
   var textWheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.PreviewMouseWheelEvent};text.RaiseEvent(textWheel);await Idle();Check(textWheel.Handled&&defaults.VerticalOffset>0,"wheel over text input scrolls containing page");
   defaults.ScrollToTop();tabs.SelectedIndex=0;page.ScrollToTop();window.Height=860;await Idle();
   for(int i=0;i<3;i++){tabs.SelectedIndex=i;await Idle();Save(view,"tab-"+i+".png");}
   vm.RawCopyFolder="unsaved-dock-preference";window.Content=null;await Idle();Check(fake.Subscribers==0,"temporary undock releases session subscription");
   window.Content=view;await Idle();Check(ReferenceEquals(vm,view.DataContext)&&vm.RawCopyFolder=="unsaved-dock-preference"&&fake.Subscribers==1,"redocking retains unsaved UI preferences and one subscription");
   var resources=new AstroOptionsResources();var manifest=new SonyA7VPlugin(null!,null!,null!,null!);Check(resources[manifest.Name+"_Options"] is DataTemplate,"options key matches actual NINA manifest name");
   var profile=DispatchProxy.Create<NINA.Profile.Interfaces.IProfileService,UiProfileProxy>();var dock=new AstroDockViewModel(profile);
   var workerDock=await Task.Run(()=>new AstroDockViewModel(profile));
   Check(workerDock.ImageGeometry.IsFrozen&&workerDock.ImageGeometry.Bounds.Width>0,"worker-composed dock icon can be bound on UI thread");
   Check(ReferenceEquals(dock.Session,SonyCameraService.Shared),"dock uses exactly the standard camera shared session");
   Check(resources[new DataTemplateKey(typeof(AstroDockViewModel))] is DataTemplate,"dock has type-keyed NINA view template");
   Check(typeof(AstroDockViewModel).GetCustomAttributes(typeof(System.ComponentModel.Composition.ExportAttribute),false).Cast<System.ComponentModel.Composition.ExportAttribute>().Any(x=>x.ContractType==typeof(NINA.Equipment.Interfaces.ViewModel.IDockableVM)),"dock exported with actual NINA contract");
   Check(new SonyA7VCamera(new SonyCameraService(),null!).HasSetupDialog,"NINA camera setup entry enabled");
   Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(x=>x.ModuleName.Equals("Cr_Core.dll",StringComparison.OrdinalIgnoreCase)),"UI harness never loads production or fake SDK");
   Check(errors.Lines.Count==0,"zero WPF binding errors: "+string.Join(";",errors.Lines));
   window.Close();await Idle();Check(fake.Subscribers==0,"unloaded UI releases session subscription");
   File.WriteAllText(Path.Combine(output,"ui-result.json"),System.Text.Json.JsonSerializer.Serialize(new{result="PASS",assertions=checks,bindingErrors=errors.Lines}));exit=0;
  }catch(Exception ex){Console.WriteLine(ex);File.WriteAllText(Path.Combine(output,"ui-failure.txt"),ex.ToString());}finally{window?.Close();app.Shutdown(exit);}}));
  app.Run();return exit;
 }
 static IEnumerable<T> Descendants<T>(DependencyObject node) where T:DependencyObject {for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);if(child is T typed)yield return typed;foreach(var descendant in Descendants<T>(child))yield return descendant;}}
 static async Task Idle()=>await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
 static void Save(FrameworkElement view,string file){view.UpdateLayout();var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var f=File.Create(Path.Combine(output,file));encoder.Save(f);}
 sealed class BindingErrors:TraceListener{public List<string> Lines=new();public override void Write(string? s){if(!string.IsNullOrEmpty(s))Lines.Add(s);}public override void WriteLine(string? s)=>Write(s);}
 sealed class FakeSession:IAstroSettingsSession {
  Action? changed;public event Action? Changed{add=>changed+=value;remove=>changed-=value;}public int Subscribers=>changed?.GetInvocationList().Length??0;
  public int Reads,Writes,Rechecks;public string Hint="";public long Nr=2;bool connected,busy;Guid id=Guid.Empty;AstroSettingChange[] receipt=Array.Empty<AstroSettingChange>();public TaskCompletionSource? BlockRead;
  public AstroUiSnapshot UiSnapshot=>new("ILCE-7M5 · 离线模拟",connected?(busy?"曝光或下载中":"空闲，可调整参数"):"未连接相机",connected,connected&&!busy,id,"7028 × 4688 · RGGB","","",receipt,Hint);
  public void SetCorrection(string value){Hint=value;changed?.Invoke();}
  public void Connect(){connected=true;id=Guid.NewGuid();changed?.Invoke();}public void SetBusy(bool value){busy=value;changed?.Invoke();}
  public async Task<IReadOnlyList<PropertyProbe>> ReadUiPropertiesAsync(){Reads++;if(BlockRead is {} t)await t.Task;return AstroUiCatalog.Definitions.Select(d=>{
   long current=d.Property switch{CameraProperty.LongExposureNoiseReduction=>Nr,CameraProperty.SilentMode=>999,CameraProperty.FocusMagnifier=>0x2800960064,CameraProperty.Slot1RemainingImages=>123,_=>0};
   long[] candidates=d.Property switch{CameraProperty.LongExposureNoiseReduction=>new long[]{1,2},CameraProperty.SilentMode=>new long[]{999},CameraProperty.Aperture=>new long[]{280,400},CameraProperty.FocusMagnifier=>new long[]{0xFFFFFFFF,0x28FFFFFFFF,0x50FFFFFFFF},_=>Array.Empty<long>()};
   return new PropertyProbe(d.Property,new(d.Property,true,d.Property is not (CameraProperty.Slot1RemainingImages or CameraProperty.FocusMagnifier),current,candidates,false,0,0,0),null);
  }).ToArray();}
  public Task<long> ApplyUiPropertyAsync(Guid connection,CameraProperty property,long expected,long requested){if(connection!=id||expected!=Nr)throw new InvalidOperationException("stale");Writes++;Nr=requested;return Task.FromResult(Nr);}
  public Task<AstroPreparationReceipt> PrepareAstroAsync(AstroPreparationOptions options){receipt=new[]{new AstroSettingChange(AstroSetting.LongExposureNoiseReductionOff,CameraProperty.LongExposureNoiseReduction,Nr,1,true,true)};Nr=1;Writes++;changed?.Invoke();return Task.FromResult((AstroPreparationReceipt)Activator.CreateInstance(typeof(AstroPreparationReceipt),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{id,receipt},null)!);}
  public Task RecheckReadyAsync(){Rechecks++;Hint="";changed?.Invoke();return Task.CompletedTask;}
  public Task RestoreAstroAsync(){Nr=receipt[0].Before;receipt=Array.Empty<AstroSettingChange>();changed?.Invoke();return Task.CompletedTask;}
 }
}

public class UiProfileProxy:DispatchProxy {
 private readonly NINA.Profile.Profile profile=new();
 protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name=="get_ActiveProfile"?profile:method?.Name.StartsWith("add_")==true||method?.Name.StartsWith("remove_")==true?null:throw new NotSupportedException(method?.Name);
}
