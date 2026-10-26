using SonyCamera.Client;
namespace SonyA7V.Camera;
public sealed record AstroUiSnapshot(string Model,string State,bool Connected,bool CanEdit,Guid ConnectionId,
    string Geometry,string LastFile,string LastError,IReadOnlyList<AstroSettingChange> Preparation,string CorrectionHint="",SensorFrame SensorFrame=SensorFrame.Unknown,string CropWarning="");
public interface IAstroSettingsSession {
    event Action? Changed;
    AstroUiSnapshot UiSnapshot {get;}
    Task<IReadOnlyList<PropertyProbe>> ReadUiPropertiesAsync();
    Task<long> ApplyUiPropertyAsync(Guid connection,CameraProperty property,long expected,long requested);
    Task<AstroPreparationReceipt> PrepareAstroAsync(AstroPreparationOptions options);
    Task RestoreAstroAsync();
    Task RecheckReadyAsync();
}
public sealed record UiChoice(long Value,string Label);
public sealed record UiPropertyDefinition(CameraProperty Property,string Name,string Hint);
public static class AstroUiCatalog {
    public static IReadOnlyList<UiPropertyDefinition> Definitions {get;}=new UiPropertyDefinition[]{
        new(CameraProperty.Aperture,"镜头光圈","仅接受当前电子镜头提供的精确光圈。"),
        new(CameraProperty.RawCompression,"RAW 压缩","切换格式可能改变图像尺寸；不要混用校准与叠加组。"),
        new(CameraProperty.LongExposureNoiseReduction,"长曝光降噪","机内降噪会增加等待；自行建立暗场库时通常关闭。"),
        new(CameraProperty.HighIsoNoiseReduction,"高 ISO 降噪","对 RAW 的影响依机型而异。"),
        new(CameraProperty.SteadyShot,"机身防抖","赤道仪或固定望远镜拍摄时通常关闭。"),
        new(CameraProperty.FocusMode,"镜头对焦模式","静态 RAW 拍摄要求 MF；镜头位置请在 NINA 对焦器面板调节。"),
        new(CameraProperty.ShutterType,"快门类型","以机身当前候选为准；切换后重新检查长曝条件。"),
        new(CameraProperty.SilentMode,"静音模式","可能与快门、长曝或降噪设置互相约束。"),
        new(CameraProperty.ApsCMode,"APS-C 裁切","会改变画幅；新尺寸以实际 RAW 为准。"),
        new(CameraProperty.LiveViewQuality,"实时预览画质","请先停止 NINA 实时预览，再更改画质。"),
        new(CameraProperty.LiveViewDisplayEffect,"预览曝光效果","关闭可辅助找星；不会改变 RAW 像素。"),
        new(CameraProperty.StillStorageDestination,"空闲机身存储位置","拍摄时插件临时启用主机接收，结束后恢复；原片副本请在默认设置中选择。"),
        new(CameraProperty.Slot1RemainingImages,"卡槽 1 剩余张数","机身提供的估计值；不代表主机磁盘余量。"),
        new(CameraProperty.Slot2RemainingImages,"卡槽 2 剩余张数","机身提供的估计值；不代表主机磁盘余量。")
    };
    public static string? Label(CameraProperty property,long value)=>property switch {
        CameraProperty.Aperture=>value>0?"f/"+(value/100.0).ToString("0.##",System.Globalization.CultureInfo.InvariantCulture):null,
        CameraProperty.RawCompression=>value switch{0=>Text.T("未压缩"),1=>Text.T("压缩"),2=>Text.T("无损压缩"),3=>Text.T("无损压缩 S"),4=>Text.T("无损压缩 M"),5=>Text.T("无损压缩 L"),6=>Text.T("压缩 HQ"),_=>null},
        CameraProperty.LongExposureNoiseReduction or CameraProperty.SteadyShot or CameraProperty.SilentMode=>value switch{1=>Text.T("关闭"),2=>Text.T("开启"),_=>null},
        CameraProperty.HighIsoNoiseReduction=>value switch{1=>Text.T("关闭"),2=>Text.T("低"),3=>Text.T("标准"),4=>Text.T("高"),_=>null},
        CameraProperty.FocusMode=>value switch{1=>Text.T("MF 手动"),2=>"AF-S",3=>"AF-C",4=>"AF-A",5=>"AF-D",6=>"DMF",7=>"PF",_=>null},
        CameraProperty.ShutterType=>value switch{1=>Text.T("自动"),2=>Text.T("机械快门"),3=>Text.T("电子快门"),_=>null},
        CameraProperty.ApsCMode=>value switch{1=>Text.T("关闭 / 全画幅"),2=>Text.T("开启 / APS-C"),3=>Text.T("自动"),_=>null},
        CameraProperty.LiveViewQuality=>value switch{0=>Text.T("标准"),1=>Text.T("高"),_=>null},
        CameraProperty.LiveViewDisplayEffect=>value switch{1=>Text.T("开启"),2=>Text.T("关闭"),_=>null},
        CameraProperty.StillStorageDestination=>value switch{1=>Text.T("主机"),2=>Text.T("存储卡"),3=>Text.T("主机与存储卡"),_=>null},
        CameraProperty.Slot1RemainingImages or CameraProperty.Slot2RemainingImages=>value is >=0 and <4294967295?value.ToString():Text.T("未知"),
        _=>null
    };
}
