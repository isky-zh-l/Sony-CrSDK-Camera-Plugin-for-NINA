namespace SonyCamera.Client;

/// <summary>Project semantic choices; vendor tokens stay in the native wrapper.</summary>
public enum AstroSetting { LongExposureNoiseReductionOff=1, HighIsoNoiseReductionOff=2, SteadyShotOff=3, ManualLensFocus=4, FullSizeLosslessRaw=5 }
public sealed record AstroPreparationOptions {
    public bool DisableLongExposureNoiseReduction { get; init; }
    public bool DisableHighIsoNoiseReduction { get; init; }
    public bool DisableSteadyShot { get; init; }
    public bool ManualLensFocus { get; init; }
    public bool FullSizeLosslessRaw { get; init; }
    internal IEnumerable<AstroSetting> Requested() {
        if(DisableLongExposureNoiseReduction)yield return AstroSetting.LongExposureNoiseReductionOff;
        if(DisableHighIsoNoiseReduction)yield return AstroSetting.HighIsoNoiseReductionOff;
        if(DisableSteadyShot)yield return AstroSetting.SteadyShotOff;
        if(ManualLensFocus)yield return AstroSetting.ManualLensFocus;
        if(FullSizeLosslessRaw)yield return AstroSetting.FullSizeLosslessRaw;
    }
}
public sealed record AstroSettingChange(AstroSetting Setting,CameraProperty Property,long Before,long Requested,bool Submitted,bool Confirmed);
/// <summary>Session-bound receipt. Partial changes on failure are recorded; no blind rollback.</summary>
public sealed class AstroPreparationReceipt {
    internal Guid Session { get; }
    internal bool Restored { get; set; }
    public IReadOnlyList<AstroSettingChange> Changes { get; }
    internal AstroPreparationReceipt(Guid session,IEnumerable<AstroSettingChange> changes) {Session=session;Changes=Array.AsReadOnly(changes.ToArray());}
}
public sealed class AstroPreparationException : InvalidOperationException {
    public AstroPreparationReceipt Receipt { get; }
    internal AstroPreparationException(AstroPreparationReceipt receipt,Exception cause):base("Astronomy preparation failed; inspect the receipt. Changed settings are retained; reconnect after an unconfirmed write.",cause){Receipt=receipt;}
}
public sealed record LensFocusStatus(CameraPropertyInfo Target,long Position,bool Driving);
