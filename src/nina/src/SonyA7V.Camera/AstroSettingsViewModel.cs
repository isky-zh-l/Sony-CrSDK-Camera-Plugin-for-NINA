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
    public string Name=>Text.T(Definition.Name);
    public string Hint=>Text.T(Definition.Hint);
    public string Current=>Info is not {Readable:true}?Text.T("当前模式或镜头未提供"):AstroUiCatalog.Label(Definition.Property,Info.Current)??Text.T("当前选项暂未识别");
    public string Availability=>Info is null?Text.T("未提供"):!Info.Readable?Text.T("不可读"):!Info.Writable?Text.T("只读"):Choices.Count==0?Text.T("无已识别可写选项"):Text.T("可调整");
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
    public string State=>Text.T(snapshot.State);
    public string Geometry=>Text.T(snapshot.Geometry);
    public string SensorFrameLabel=>Text.T(snapshot.SensorFrame switch {SensorFrame.FullFrame=>"Full frame",SensorFrame.ApsC=>"APS-C",_=>"Unknown"});
    public string CropWarning=>snapshot.CropWarning;
    private string language="auto";
    public string Language {get=>language;set {language=value;Text.SetLanguage(value);Raise("");}}
    public IReadOnlyList<KeyValuePair<string,string>> Languages=>new[]{new KeyValuePair<string,string>("auto",Text.T("Automatic")),new("zh","中文"),new("en","English")};
    public bool RequireFullFrame {get;set;}

    public string LastFile=>string.IsNullOrEmpty(snapshot.LastFile)?Text.T("尚无本连接的图像"):snapshot.LastFile;
    public string CameraError=>Text.T(snapshot.LastError);
    public string CorrectionHint=>snapshot.CorrectionHint;
    public IReadOnlyList<string> PreparationDetails=>snapshot.Preparation.Select(x=>(AstroUiCatalog.Definitions.FirstOrDefault(d=>d.Property==x.Property)?.Name is {} name?Text.T(name):x.Setting.ToString())+"："+(AstroUiCatalog.Label(x.Property,x.Before)??Text.T("未知"))+" → "+(AstroUiCatalog.Label(x.Property,x.Requested)??Text.T("未知"))+" · "+(!x.Submitted?Text.T("保持不变"):x.Confirmed?Text.T("已确认"):Text.T("未确认"))).ToArray();
    public string PreparationState=>snapshot.Preparation.Count==0?Text.T("本连接尚无准备回执"):Text.F($"已提交 {snapshot.Preparation.Count(x=>x.Submitted)} 项，已确认 {snapshot.Preparation.Count(x=>x.Submitted&&x.Confirmed)} 项");
    private string message="";
    public string Message {get=>Text.T(message);private set{message=value;Raise();}}
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
    public IReadOnlyList<KeyValuePair<string,string>> RawSaveModes=>new[]{new KeyValuePair<string,string>("never",Text.T("仅保存 NINA 图像")),new KeyValuePair<string,string>("also",Text.T("NINA 图像 + ARW 副本")),new KeyValuePair<string,string>("only",Text.T("向 NINA 交付原始 ARW"))};
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
        session.Changed+=OnSessionChanged;Text.Changed+=OnLanguageChanged;subscribed=true;
        try {LoadDefaults();}catch(Exception ex){SetError(ex);}
    }
    private void OnLanguageChanged() {
        if(disposed||dispatcher.HasShutdownStarted)return;
        if(!dispatcher.CheckAccess()){dispatcher.BeginInvoke(new Action(OnLanguageChanged));return;}
        var rows=Rows.Select(row=>(row.Definition,row.Info,row.ConnectionId,selected:row.Selected?.Value)).ToArray();
        Rows.Clear();
        foreach(var row in rows){var updated=new AstroParameterRow(row.Definition,new(row.Definition.Property,row.Info,null),row.ConnectionId,()=>ApplyRowCommand.Refresh());updated.Selected=updated.Choices.FirstOrDefault(x=>x.Value==row.selected);Rows.Add(updated);}
        UpdateSnapshot();Raise("");
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
    private void SetError(Exception ex){IsError=true;Raise(nameof(IsError));Message=Text.Error(ex);}
    private async Task RunAsync(Func<Task> action) {
        if(Busy)return;Busy=true;IsError=false;Raise(nameof(IsError));Message=Text.T("处理中…");
        try {await action();}catch(Exception ex){SetError(ex);}finally {Busy=false;UpdateSnapshot();}
    }
    private async Task ReadRowsAsync() {
        var id=session.UiSnapshot.ConnectionId;
        var probes=await session.ReadUiPropertiesAsync();
        if(id!=session.UiSnapshot.ConnectionId)throw new InvalidOperationException(Text.T("连接已改变，请重新刷新"));
        Rows.Clear();
        foreach(var definition in AstroUiCatalog.Definitions) {
            var probe=probes.FirstOrDefault(x=>x.Property==definition.Property)??new(definition.Property,null,null);
            Rows.Add(new(definition,probe,id,()=>ApplyRowCommand.Refresh()));
        }
    }
    public Task RecheckAsync()=>RunAsync(async()=>{await session.RecheckReadyAsync();await ReadRowsAsync();Message=Text.T("拍摄条件已通过检查；可以重试拍摄，不会自动重拍。");});
    public Task RefreshAsync()=>RunAsync(async()=>{await ReadRowsAsync();Message=Text.T("已读回相机当前参数；不可用与只读项目不会写入。");});
    public Task ApplyRowAsync(AstroParameterRow row)=>RunAsync(async()=>{
        if(!CanWriteDuringOperation(row))throw new InvalidOperationException(Text.T("参数不可写或连接已变化，请刷新后选择有效候选"));
        await session.ApplyUiPropertyAsync(row.ConnectionId,row.Definition.Property,row.Info!.Current,row.Selected!.Value);
        await ReadRowsAsync();Message=Text.T("相机已接受参数并完成回读。");
    });
    private bool CanWriteDuringOperation(AstroParameterRow row)=>snapshot.CanEdit&&row.CanApply&&row.ConnectionId==snapshot.ConnectionId;
    private AstroPreparationOptions Plan()=>new(){DisableLongExposureNoiseReduction=LongNrOff,DisableHighIsoNoiseReduction=HighIsoNrOff,DisableSteadyShot=SteadyShotOff,ManualLensFocus=ManualFocus,FullSizeLosslessRaw=LosslessRaw};
    public Task PrepareAsync()=>RunAsync(async()=>{
        if(!session.UiSnapshot.CanEdit)throw new InvalidOperationException(Text.T("请先停止曝光、预览或镜头移动"));
        await session.PrepareAstroAsync(Plan());await ReadRowsAsync();Message=Text.T("所选天文准备已应用并回读；可在本连接恢复原设置。");
    });
    public Task RestoreAsync()=>RunAsync(async()=>{await session.RestoreAstroAsync();await ReadRowsAsync();Message=Text.T("本连接的准备设置已恢复。");});
    public Task SaveAsync()=>RunAsync(()=>{
        var plan=Plan();
        var updated=document.Settings with{Language=Language,RequireFullFrame=RequireFullFrame,CameraModel=string.IsNullOrWhiteSpace(CameraModel)?null:CameraModel.Trim(),SaveOriginalRaw=SaveOriginalRaw,RawCopyFolder=RawCopyFolder.Trim(),LiveViewColor=LiveViewColor,EnableLensFocuser=EnableLensFocuser,
            PixelSizeMicrons=string.IsNullOrWhiteSpace(PixelSize)?null:double.Parse(PixelSize,CultureInfo.InvariantCulture),
            SensorWidth=string.IsNullOrWhiteSpace(SensorWidth)?null:int.Parse(SensorWidth,CultureInfo.InvariantCulture),SensorHeight=string.IsNullOrWhiteSpace(SensorHeight)?null:int.Parse(SensorHeight,CultureInfo.InvariantCulture),BayerPattern=BayerPattern,
            AstroPreparationSelection=plan,AstroPreparation=AutoPrepare?plan:null};
        document=store.Save(updated,document.Sha256);Message=Text.T("默认设置已保存；下次连接相机生效。当前相机参数未改变。");return Task.CompletedTask;
    });
    public Task ReloadAsync()=>RunAsync(()=>{LoadDefaults();Message=Text.T("已重新载入保存的默认设置。");return Task.CompletedTask;});
    private void LoadDefaults() {
        document=store.Read();var v=document.Settings;var plan=v.AstroPreparation??v.AstroPreparationSelection??new();
        Language=v.Language;RequireFullFrame=v.RequireFullFrame;
        CameraModel=v.CameraModel??"";SaveOriginalRaw=v.SaveOriginalRaw;RawCopyFolder=v.RawCopyFolder;LiveViewColor=v.LiveViewColor;EnableLensFocuser=v.EnableLensFocuser;
        PixelSize=v.PixelSizeMicrons?.ToString(CultureInfo.InvariantCulture)??"";SensorWidth=v.SensorWidth?.ToString()??"";SensorHeight=v.SensorHeight?.ToString()??"";BayerPattern=v.BayerPattern;
        AutoPrepare=v.AstroPreparation is not null;LongNrOff=plan.DisableLongExposureNoiseReduction;HighIsoNrOff=plan.DisableHighIsoNoiseReduction;SteadyShotOff=plan.DisableSteadyShot;ManualFocus=plan.ManualLensFocus;LosslessRaw=plan.FullSizeLosslessRaw;
        Raise("");
    }
    public void Suspend(){if(subscribed){session.Changed-=OnSessionChanged;Text.Changed-=OnLanguageChanged;subscribed=false;}}
    public void Resume(){ObjectDisposedException.ThrowIf(disposed,this);if(!subscribed){session.Changed+=OnSessionChanged;Text.Changed+=OnLanguageChanged;subscribed=true;}UpdateSnapshot();}
    public void Dispose(){Suspend();disposed=true;}
}
