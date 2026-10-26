using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.WPF.Base.Interfaces.ViewModel;
using SonyCamera.Client;

namespace SonyA7V.Camera;

/// <summary>Optional camera-controlled lens, sharing the connected camera session.
/// SDK position units are not calibrated physical steps. No claim of absolute focus repeatability.</summary>
public sealed class SonyLensFocuser(SonyCameraService service) : IFocuser {
    private LensFocusStatus? last;
    private bool attached;
    private Guid connectionId;
    private int moving;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName]string? name=null){try{PropertyChanged?.Invoke(this,new(name));}catch(Exception ex){Logger.Error("[SonyCrSDK] Lens focuser observer: "+ex.Message,ex);}}
    public string Id=>"SonyCrSDK-Shared-Lens-Focus";
    public string Name=>"Sony CrSDK Lens Focus";
    public string DisplayName=>$"{service.Model} lens (CrSDK, experimental)";
    public string Category=>"Sony";
    public string Description=>"Camera-controlled lens; connect the Sony camera first. Requires manual focus and SDK absolute position/idle feedback. Halt during an absolute move is unavailable; position units are not micrometres.";
    public string DriverInfo=>"Shared Sony CrSDK camera session; absolute lens position readback";
    public string DriverVersion=>typeof(SonyLensFocuser).Assembly.GetName().Version?.ToString()??"unknown";
    public bool HasSetupDialog=>true;
    public void SetupDialog()=>AstroSettingsWindow.Show(service);
    public bool Connected=>attached&&service.Connected&&service.Settings.EnableLensFocuser&&connectionId==service.ConnectionId;
    public bool IsMoving=>Volatile.Read(ref moving)!=0||Connected&&last?.Driving==true;
    public int Position=>Connected&&last is not null?checked((int)last.Position):-1;
    public int MaxStep=>Connected&&last is not null?checked((int)last.Target.Maximum):0;
    public int MaxIncrement=>Connected&&last is not null?checked((int)(last.Target.Maximum-last.Target.Minimum)):0;
    public double StepSize=>double.NaN;
    public bool TempCompAvailable=>false;
    public bool TempComp {get=>false;set=>throw new NotSupportedException(Text.T("No lens temperature compensation"));}
    public double Temperature=>double.NaN;
    public IList<string> SupportedActions=>Array.Empty<string>();
    public string Action(string actionName,string actionParameters)=>throw new NotSupportedException(actionName);
    public void SendCommandBlind(string command,bool raw=true)=>throw new NotSupportedException(Text.T("No raw command pass-through"));
    public bool SendCommandBool(string command,bool raw=true)=>throw new NotSupportedException(Text.T("No raw command pass-through"));
    public string SendCommandString(string command,bool raw=true)=>throw new NotSupportedException(Text.T("No raw command pass-through"));
    public async Task<bool> Connect(CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if(!service.Connected||!service.Settings.EnableLensFocuser)throw new InvalidOperationException(Text.T("Enable enableLensFocuser in settings.json, reconnect the Sony camera, then connect the lens focuser"));
        last=await service.GetLensFocusAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if(last.Driving)throw new InvalidOperationException(Text.T("Lens is already moving"));
        connectionId=service.ConnectionId;attached=true;Raise(nameof(Connected));Raise(nameof(Position));return true;
    }
    public void Disconnect() {
        if(IsMoving)throw new InvalidOperationException(Text.T("Wait for the in-flight absolute move; SDK does not provide a verified absolute Halt"));
        attached=false;last=null;Raise(nameof(Connected));Raise(nameof(Position));
        // Detaching the focuser never disconnects the camera shared by NINA's imager.
    }
    public async Task Move(int position,CancellationToken ct,int waitInMs=1000) {
        if(!Connected)throw new InvalidOperationException(Text.T("Lens focuser is not connected"));
        if(waitInMs<0||waitInMs>60000)throw new ArgumentOutOfRangeException(nameof(waitInMs));
        if(Interlocked.CompareExchange(ref moving,1,0)!=0)throw new InvalidOperationException(Text.T("A lens move is already in progress"));
        Raise(nameof(IsMoving));
        try {
            last=await service.MoveLensFocusAsync(position,ct).ConfigureAwait(false);
            Raise(nameof(Position));
            if(waitInMs>0)await Task.Delay(waitInMs,ct).ConfigureAwait(false);
        }catch {
            // Do not leave an old position advertised after an uncertain/cancelled motion.
            attached=false;last=null;Raise(nameof(Connected));throw;
        }finally {Interlocked.Exchange(ref moving,0);Raise(nameof(IsMoving));}
    }
    public void Halt() {
        if(IsMoving)throw new NotSupportedException(Text.T("Sony SDK does not document a verified stop for an absolute focus-position request. The owned move continues until actual position and idle are confirmed; relative pulse stop is a different operation."));
    }
}

[Export(typeof(IEquipmentProvider<IFocuser>))]
[Export(typeof(IEquipmentProvider))]
public sealed class SonyLensFocuserProvider : IEquipmentProvider<IFocuser>, IEquipmentProvider {
    private readonly SonyLensFocuser focuser=new(SonyCameraService.Shared);
    public string Name=>"Sony CrSDK Lens Focus (experimental)";
    public IList<IFocuser> GetEquipment()=>new List<IFocuser>{focuser};
}
