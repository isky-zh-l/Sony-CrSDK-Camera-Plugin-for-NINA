using System.Globalization;
using System.Text.Json;
namespace SonyCamera.Client;
/// <summary>Shared, UI-framework-independent resources. Logs retain vendor codes and identities.</summary>
public static class Text {
    private static string language="auto",hostLanguage=CultureInfo.CurrentUICulture.Name;
    private static readonly Dictionary<string,Dictionary<string,string>> catalog=Load();
    public static event Action? Changed;
    public static string Language=>language;
    public static string EffectiveLanguage=>Resolve(language,hostLanguage);
    public static string Resolve(string choice,string? host)=>choice=="zh"||choice=="auto"&&host?.StartsWith("zh",StringComparison.OrdinalIgnoreCase)==true?"zh":"en";
    public static void SetLanguage(string value){if(value is not("auto" or "zh" or "en"))value="auto";if(language==value)return;language=value;Changed?.Invoke();}
    public static void SetHostLanguage(string? value){value??="en";if(hostLanguage==value)return;hostLanguage=value;Changed?.Invoke();}
    private static Dictionary<string,Dictionary<string,string>> Load(){using var stream=typeof(Text).Assembly.GetManifestResourceStream("SonyCamera.Client.Texts.json")!;return JsonSerializer.Deserialize<Dictionary<string,Dictionary<string,string>>>(stream)!;}
    public static string T(string value)=>catalog.TryGetValue(value,out var entry)?entry[EffectiveLanguage]:value;
    /// <summary>Formats positional .NET interpolation holes; translations may reorder or repeat them.</summary>
    public static string F(FormattableString value) =>
        string.Format(CultureInfo.CurrentCulture, T(value.Format), value.GetArguments());
    public static string Error(Exception error) {
        if(error is CameraException camera)return CameraError(camera.Error);
        return T(error.Message);
    }
    public static string CameraError(CameraError error) {
        var detail=T(error.Message);
        if(detail==error.Message && EffectiveLanguage=="zh")detail="相机操作失败，请根据错误码检查机身设置、连接及日志";
        return detail+$" [code={error.Code}, Sony=0x{error.SonyCode:X8}, stage={error.Stage}, capture={error.CaptureId}]";
    }
}
