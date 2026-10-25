using SonyCamera.Client;
namespace SonyA7V.Camera;

public sealed class CameraCorrectionRequiredException(string message, Exception cause)
    : InvalidOperationException(message, cause);

public sealed partial class SonyCameraService {
    private string correctionHint = "";
    public string CorrectionHint {get {lock(stateGate)return correctionHint;}}

    private static bool IsCorrectablePreflight(CameraException ex) =>
        ex.Error.Code == ProjectCode.Unsupported && ex.Error.CaptureId == 0 &&
        (ex.Error.Message.StartsWith("Requires ", StringComparison.Ordinal) ||
         ex.Error.Message.StartsWith("Unrecognised RAW compression", StringComparison.Ordinal));

    private void ClearCorrection() {lock(stateGate){correctionHint="";lastError="";}}

    private CameraCorrectionRequiredException RequireCorrection(Exception cause, string? hint = null) {
        hint ??= cause.Message.Contains("(MF)", StringComparison.Ordinal)
            ? "请将镜头/机身切换到 MF 手动对焦，然后点击重新检查或重试拍摄。连接保留，不会自动重拍。"
            : cause.Message.Contains("M manual", StringComparison.Ordinal)
            ? "请将相机切换到 M 手动曝光，然后点击重新检查或重试拍摄。连接保留。"
            : cause.Message.Contains("RAW", StringComparison.Ordinal)
            ? "请检查 RAW 格式设置（仅 RAW、有效压缩档），然后点击重新检查或重试。连接保留。"
            : "请按错误提示修正相机设置，然后点击重新检查或重试。连接保留，不会自动重拍。";
        lock(stateGate){lastError=cause.Message;correctionHint=hint;}
        return new CameraCorrectionRequiredException(hint + " 原因：" + cause.Message, cause);
    }

    private async Task CheckCaptureReadyCoreAsync() {
        try {await client!.CheckCaptureReadyAsync().ConfigureAwait(false);}
        catch(CameraException ex) when(IsCorrectablePreflight(ex)) {throw RequireCorrection(ex);}
    }

    public Task RecheckReadyAsync() => CoordinateAsync("重新检查拍摄条件", async () => {
        RequireIdleSettings();
        await CheckCaptureReadyCoreAsync().ConfigureAwait(false);
        Facts=Facts! with {
            Parameters=await client!.ReadParametersAsync().ConfigureAwait(false),
            Isos=await client.GetSupportedIsoAsync().ConfigureAwait(false),
            Shutters=await client.GetSupportedExposureSecondsAsync().ConfigureAwait(false),
            Capabilities=await client.GetExposureCapabilitiesAsync().ConfigureAwait(false)
        };
        ClearCorrection();
    });
}
