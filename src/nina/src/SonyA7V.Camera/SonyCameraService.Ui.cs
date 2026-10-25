using System.IO;
using SonyCamera.Client;
namespace SonyA7V.Camera;
public sealed partial class SonyCameraService : IAstroSettingsSession {
    private int lensMoving;
    public AstroUiSnapshot UiSnapshot {
        get {
            bool idle=CurrentOperation is null&&Connected&&!Unconfirmed&&client?.State==SessionState.Connected&&!LiveViewActive&&Volatile.Read(ref lensMoving)==0&&active is not {IsCompleted:false};
            string state=CurrentOperation??(Unconfirmed?"状态未确认，请重新连接":!Connected?"未连接相机":Volatile.Read(ref lensMoving)!=0?"镜头移动中":LiveViewActive?"实时预览中":CorrectionHint.Length>0?"已连接，等待修正设置":idle?"空闲，可调整参数":"曝光或下载中");
            return new(Model,state,Connected,idle,ConnectionId,Geometry is {} g?$"{g.Width} × {g.Height} · {g.Cfa}":"等待首张 RAW 确认",Path.GetFileName(LastCapture?.ArwPath??""),LastError,PreparationReceipt?.Changes??Array.Empty<AstroSettingChange>(),CorrectionHint);
        }
    }
    public async Task<IReadOnlyList<PropertyProbe>> ReadUiPropertiesAsync() {
        await EnterOperationAsync("读取相机参数").ConfigureAwait(false);
        try {
            RequireIdleSettings();if(Volatile.Read(ref lensMoving)!=0)throw new InvalidOperationException("请等待镜头停止");
            var result=await client!.InspectPropertiesAsync(AstroUiCatalog.Definitions.Select(x=>x.Property).ToArray()).ConfigureAwait(false);
            await RefreshBatteryCoreAsync().ConfigureAwait(false);return result;
        }catch(Exception ex){lock(stateGate)lastError=ex.Message;throw;}
        finally {ExitOperation();NotifyChanged();}
    }
    public async Task<long> ApplyUiPropertyAsync(Guid connection,CameraProperty property,long expected,long requested) {
        await EnterOperationAsync("应用相机参数").ConfigureAwait(false);
        try {
            RequireIdleSettings();
            if(connection!=ConnectionId)throw new InvalidOperationException("连接已改变，请刷新参数后再选择");
            if(!AstroUiCatalog.Definitions.Any(x=>x.Property==property)||property is CameraProperty.Slot1RemainingImages or CameraProperty.Slot2RemainingImages)throw new NotSupportedException("此界面不提供该参数写入");
            if(PreparationReceipt?.Changes.Any(x=>x.Submitted&&x.Property==property)==true)throw new InvalidOperationException("该参数属于本连接的天文准备回执，请先恢复准备设置再单独调整");
            var actual=await client!.SetPropertyIfCurrentAsync(property,expected,requested).ConfigureAwait(false);
            Facts=Facts! with{Parameters=await client.ReadParametersAsync().ConfigureAwait(false)};
            return actual;
        }catch(Exception ex){lock(stateGate)lastError=ex.Message;throw;}
        finally {ExitOperation();NotifyChanged();}
    }
}
