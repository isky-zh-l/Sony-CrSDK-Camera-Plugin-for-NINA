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
    /// <summary>
    /// Formats an interpolated message. Interpolated holes keep their source names (<c>{path}</c>), and
    /// <see cref="string.Format(string,object[])"/> only accepts positional holes, so the template is
    /// expanded here. Arguments are consumed in the order the holes appear; translations must keep that
    /// order and the optional <c>:format</c> specifier.
    /// </summary>
    public static string F(FormattableString value) {
        var template = T(value.Format);
        var arguments = value.GetArguments();
        if(template.IndexOf('{') < 0)return template;
        var builder = new System.Text.StringBuilder(template.Length+16);
        var next = 0;
        for(var i=0;i<template.Length;i++) {
            var current = template[i];
            if(current=='{') {
                if(i+1<template.Length && template[i+1]=='{') { builder.Append('{'); i++; continue; }
                var close = template.IndexOf('}',i+1);
                if(close<0) { builder.Append(template,i,template.Length-i); break; }
                var hole = template.Substring(i+1,close-i-1);
                var specifier = "";
                var colon = hole.IndexOf(':');
                if(colon>=0) { specifier=hole.Substring(colon+1); hole=hole.Substring(0,colon); }
                var alignment = 0;
                var comma = hole.IndexOf(',');
                if(comma>=0) { _ = int.TryParse(hole.AsSpan(comma+1),NumberStyles.Integer,CultureInfo.InvariantCulture,out alignment); hole=hole.Substring(0,comma); }
                var argument = next<arguments.Length ? arguments[next] : null;
                next++;
                var text = specifier.Length>0 && argument is IFormattable formattable
                    ? formattable.ToString(specifier,CultureInfo.CurrentCulture)
                    : Convert.ToString(argument,CultureInfo.CurrentCulture) ?? "";
                builder.Append(alignment>0 ? text.PadLeft(alignment) : alignment<0 ? text.PadRight(-alignment) : text);
                i = close;
            }
            else if(current=='}' && i+1<template.Length && template[i+1]=='}') { builder.Append('}'); i++; }
            else builder.Append(current);
        }
        return builder.ToString();
    }
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
