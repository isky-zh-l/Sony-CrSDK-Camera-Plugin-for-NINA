using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using SonyCamera.Client;

namespace SonyA7V.Camera;
public abstract class UiObservable : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}
public sealed class UiAsyncCommand(Func<object?,Task> execute,Func<object?,bool> canExecute):ICommand {
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p)=>canExecute(p);
    public async void Execute(object? p){if(CanExecute(p))await execute(p);}
    public void Refresh()=>CanExecuteChanged?.Invoke(this,EventArgs.Empty);
}
public sealed class AstroParameterRow : UiObservable {
    public UiPropertyDefinition Definition {get;}
    public CameraPropertyInfo? Info {get;}
    public Guid ConnectionId {get;}
    public string Name=>Definition.Name;
    public string Hint=>Definition.Hint;
    public string Current=>Info is not {Readable:true}?"当前模式或镜头未提供":AstroUiCatalog.Label(Definition.Property,Info.Current)??"当前选项暂未识别";
    public string Availability=>Info is null?"未提供":!Info.Readable?"不可读":!Info.Writable?"只读":Choices.Count==0?"无已识别可写选项":"可调整";
    public IReadOnlyList<UiChoice> Choices {get;}
    private UiChoice? selected;
    private readonly Action changed;
    public UiChoice? Selected {get=>selected;set{selected=value;Raise();Raise(nameof(CanApply));changed();}}
    public bool CanSelect=>Info is {Readable:true,Writable:true}&&Choices.Count>0&&!(Definition.Property==CameraProperty.Aperture&&Info.Current==0);
    public bool CanApply=>Info is {Readable:true,Writable:true}&&Selected is {} choice&&Info.Accepts(choice.Value)&&choice.Value!=Info.Current&&!(Definition.Property==CameraProperty.Aperture&&Info.Current==0);
    public AstroParameterRow(UiPropertyDefinition definition,PropertyProbe probe,Guid connection,Action changed) {
        Definition=definition;Info=probe.Info;ConnectionId=connection;this.changed=changed;
        var values=Info?.Candidates??Array.Empty<long>();
        // Do not expand camera ranges into enormous lists or invent unnamed values.
        Choices=values.Select(v=>new{Value=v,Label=AstroUiCatalog.Label(definition.Property,v)}).Where(x=>x.Label is not null).Select(x=>new UiChoice(x.Value,x.Label!)).ToArray();
        selected=Choices.FirstOrDefault(x=>x.Value==Info?.Current);
    }
}
public sealed class AstroSettingsViewModel : UiObservable, IDisposable {
    private readonly IAstroSettingsSession session;
    private readonly PluginSettingsStore store;
    private readonly Dispatcher dispatcher;
    private PluginSettingsDocument document=new(new(),"missing");
    private AstroUiSnapshot snapshot;
    private bool disposed,busy,subscribed;
    public string Version=>typeof(AstroSettingsViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion.Split('+')[0]??typeof(AstroSettingsViewModel).Assembly.GetName().Version!.ToString();
    public bool Busy {get=>busy;private set{busy=value;NotifyState();}}
    public bool CanEdit=>!Busy&&snapshot.CanEdit;
    public bool CanRestore=>CanEdit&&snapshot.Preparation.Any(x=>x.Submitted)&&snapshot.Preparation.All(x=>!x.Submitted||x.Confirmed);
    public string Model=>snapshot.Model;
    public string State=>snapshot.State;
    public string Geometry=>snapshot.Geometry;
    public string LastFile=>string.IsNullOrEmpty(snapshot.LastFile)?"尚无本连接的图像":snapshot.LastFile;
    public string CameraError=>snapshot.LastError;
    public string CorrectionHint=>snapshot.CorrectionHint;
    public IReadOnlyList<string> PreparationDetails=>snapshot.Preparation.Select(x=>(AstroUiCatalog.Definitions.FirstOrDefault(d=>d.Property==x.Property)?.Name??x.Setting.ToString())+"："+(AstroUiCatalog.Label(x.Property,x.Before)??"未知")+" → "+(AstroUiCatalog.Label(x.Property,x.Requested)??"未知")+" · "+(!x.Submitted?"保持不变":x.Confirmed?"已确认":"未确认")).ToArray();
    public string PreparationState=>snapshot.Preparation.Count==0?"本连接尚无准备回执":$"已提交 {snapshot.Preparation.Count(x=>x.Submitted)} 项，已确认 {snapshot.Preparation.Count(x=>x.Submitted&&x.Confirmed)} 项";
    private string message="";
    public string Message {get=>message;private set{message=value;Raise();}}
    public bool IsError {get;private set;}
    public ObservableCollection<AstroParameterRow> Rows {get;}=new();
    public UiAsyncCommand RefreshCommand {get;}
    public UiAsyncCommand RecheckCommand {get;}
    public UiAsyncCommand ApplyRowCommand {get;}
    public UiAsyncCommand PrepareCommand {get;}
    public UiAsyncCommand RestoreCommand {get;}
    public UiAsyncCommand SaveCommand {get;}
    public UiAsyncCommand ReloadCommand {get;}
    public string CameraModel {get;set;}="";
    public IReadOnlyList<string> Models=>new[]{""}.Concat(CameraSupport.SdkModels).ToArray();
    public string SaveOriginalRaw {get;set;}="never";
    public IReadOnlyList<KeyValuePair<string,string>> RawSaveModes {get;}=new[]{new KeyValuePair<string,string>("never","仅保存 NINA 图像"),new KeyValuePair<string,string>("also","NINA 图像 + ARW 副本"),new KeyValuePair<string,string>("only","向 NINA 交付原始 ARW")};
    public string RawCopyFolder {get;set;}="raw";
    public bool LiveViewColor {get;set;}
    public bool EnableLensFocuser {get;set;}
    public bool AutoPrepare {get;set;}
    public bool LongNrOff {get;set;}
    public bool HighIsoNrOff {get;set;}
    public bool SteadyShotOff {get;set;}
    public bool ManualFocus {get;set;}
    public bool LosslessRaw {get;set;}
    public string PixelSize {get;set;}="";
    public string SensorWidth {get;set;}="";
    public string SensorHeight {get;set;}="";
    public string BayerPattern {get;set;}="RGGB";
    public IReadOnlyList<string> BayerPatterns {get;}=new[]{"RGGB","BGGR","GRBG","GBRG"};
    public AstroSettingsViewModel(IAstroSettingsSession session,PluginSettingsStore store,Dispatcher dispatcher) {
        this.session=session;this.store=store;this.dispatcher=dispatcher;snapshot=session.UiSnapshot;
        RefreshCommand=new(_=>RefreshAsync(),_=>CanEdit);
        RecheckCommand=new(_=>RecheckAsync(),_=>CanEdit);
        ApplyRowCommand=new(p=>ApplyRowAsync((AstroParameterRow)p!),p=>CanEdit&&p is AstroParameterRow row&&row.CanApply&&row.ConnectionId==snapshot.ConnectionId);
        PrepareCommand=new(_=>PrepareAsync(),_=>CanEdit);
        RestoreCommand=new(_=>RestoreAsync(),_=>CanRestore);
        SaveCommand=new(_=>SaveAsync(),_=>!Busy);ReloadCommand=new(_=>ReloadAsync(),_=>!Busy);
        session.Changed+=OnSessionChanged;subscribed=true;
        try {LoadDefaults();}catch(Exception ex){SetError(ex);}
    }
    private void OnSessionChanged() {
        if(disposed||dispatcher.HasShutdownStarted)return;
        if(dispatcher.CheckAccess())UpdateSnapshot();else dispatcher.BeginInvoke(new Action(()=>{if(!disposed)UpdateSnapshot();}));
    }
    private void UpdateSnapshot() {
        var next=session.UiSnapshot;
        if(next.ConnectionId!=snapshot.ConnectionId||!next.Connected)Rows.Clear();
        snapshot=next;Raise("");NotifyState();
    }
    private void NotifyState() {
        Raise(nameof(Busy));Raise(nameof(CanEdit));Raise(nameof(CanRestore));
        RefreshCommand?.Refresh();RecheckCommand?.Refresh();ApplyRowCommand?.Refresh();PrepareCommand?.Refresh();RestoreCommand?.Refresh();SaveCommand?.Refresh();ReloadCommand?.Refresh();
    }
    private void SetError(Exception ex){IsError=true;Raise(nameof(IsError));Message=ex.GetBaseException().Message;}
    private async Task RunAsync(Func<Task> action) {
        if(Busy)return;Busy=true;IsError=false;Raise(nameof(IsError));Message="处理中…";
        try {await action();}catch(Exception ex){SetError(ex);}finally {Busy=false;UpdateSnapshot();}
    }
    private async Task ReadRowsAsync() {
        var id=session.UiSnapshot.ConnectionId;
        var probes=await session.ReadUiPropertiesAsync();
        if(id!=session.UiSnapshot.ConnectionId)throw new InvalidOperationException("连接已改变，请重新刷新");
        Rows.Clear();
        foreach(var definition in AstroUiCatalog.Definitions) {
            var probe=probes.FirstOrDefault(x=>x.Property==definition.Property)??new(definition.Property,null,null);
            Rows.Add(new(definition,probe,id,()=>ApplyRowCommand.Refresh()));
        }
    }
    public Task RecheckAsync()=>RunAsync(async()=>{await session.RecheckReadyAsync();await ReadRowsAsync();Message="拍摄条件已通过检查；可以重试拍摄，不会自动重拍。";});
    public Task RefreshAsync()=>RunAsync(async()=>{await ReadRowsAsync();Message="已读回相机当前参数；不可用与只读项目不会写入。";});
    public Task ApplyRowAsync(AstroParameterRow row)=>RunAsync(async()=>{
        if(!CanWriteDuringOperation(row))throw new InvalidOperationException("参数不可写或连接已变化，请刷新后选择有效候选");
        await session.ApplyUiPropertyAsync(row.ConnectionId,row.Definition.Property,row.Info!.Current,row.Selected!.Value);
        await ReadRowsAsync();Message="相机已接受参数并完成回读。";
    });
    private bool CanWriteDuringOperation(AstroParameterRow row)=>snapshot.CanEdit&&row.CanApply&&row.ConnectionId==snapshot.ConnectionId;
    private AstroPreparationOptions Plan()=>new(){DisableLongExposureNoiseReduction=LongNrOff,DisableHighIsoNoiseReduction=HighIsoNrOff,DisableSteadyShot=SteadyShotOff,ManualLensFocus=ManualFocus,FullSizeLosslessRaw=LosslessRaw};
    public Task PrepareAsync()=>RunAsync(async()=>{
        if(!session.UiSnapshot.CanEdit)throw new InvalidOperationException("请先停止曝光、预览或镜头移动");
        await session.PrepareAstroAsync(Plan());await ReadRowsAsync();Message="所选天文准备已应用并回读；可在本连接恢复原设置。";
    });
    public Task RestoreAsync()=>RunAsync(async()=>{await session.RestoreAstroAsync();await ReadRowsAsync();Message="本连接的准备设置已恢复。";});
    public Task SaveAsync()=>RunAsync(()=>{
        var plan=Plan();
        var updated=document.Settings with{CameraModel=string.IsNullOrWhiteSpace(CameraModel)?null:CameraModel.Trim(),SaveOriginalRaw=SaveOriginalRaw,RawCopyFolder=RawCopyFolder.Trim(),LiveViewColor=LiveViewColor,EnableLensFocuser=EnableLensFocuser,
            PixelSizeMicrons=string.IsNullOrWhiteSpace(PixelSize)?null:double.Parse(PixelSize,CultureInfo.InvariantCulture),
            SensorWidth=string.IsNullOrWhiteSpace(SensorWidth)?null:int.Parse(SensorWidth,CultureInfo.InvariantCulture),SensorHeight=string.IsNullOrWhiteSpace(SensorHeight)?null:int.Parse(SensorHeight,CultureInfo.InvariantCulture),BayerPattern=BayerPattern,
            AstroPreparationSelection=plan,AstroPreparation=AutoPrepare?plan:null};
        document=store.Save(updated,document.Sha256);Message="默认设置已保存；下次连接相机生效。当前相机参数未改变。";return Task.CompletedTask;
    });
    public Task ReloadAsync()=>RunAsync(()=>{LoadDefaults();Message="已重新载入保存的默认设置。";return Task.CompletedTask;});
    private void LoadDefaults() {
        document=store.Read();var v=document.Settings;var plan=v.AstroPreparation??v.AstroPreparationSelection??new();
        CameraModel=v.CameraModel??"";SaveOriginalRaw=v.SaveOriginalRaw;RawCopyFolder=v.RawCopyFolder;LiveViewColor=v.LiveViewColor;EnableLensFocuser=v.EnableLensFocuser;
        PixelSize=v.PixelSizeMicrons?.ToString(CultureInfo.InvariantCulture)??"";SensorWidth=v.SensorWidth?.ToString()??"";SensorHeight=v.SensorHeight?.ToString()??"";BayerPattern=v.BayerPattern;
        AutoPrepare=v.AstroPreparation is not null;LongNrOff=plan.DisableLongExposureNoiseReduction;HighIsoNrOff=plan.DisableHighIsoNoiseReduction;SteadyShotOff=plan.DisableSteadyShot;ManualFocus=plan.ManualLensFocus;LosslessRaw=plan.FullSizeLosslessRaw;
        Raise("");
    }
    public void Suspend(){if(subscribed){session.Changed-=OnSessionChanged;subscribed=false;}}
    public void Resume(){ObjectDisposedException.ThrowIf(disposed,this);if(!subscribed){session.Changed+=OnSessionChanged;subscribed=true;}UpdateSnapshot();}
    public void Dispose(){Suspend();disposed=true;}
}
