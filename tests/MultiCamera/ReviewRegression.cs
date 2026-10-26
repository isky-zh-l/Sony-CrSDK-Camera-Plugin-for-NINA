using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SonyA7V.Camera;
using SonyCamera.Client;

internal static partial class Program {
    static void ReviewRegression() {
        var savedLanguage=Text.Language;
        try {
            Check(Text.Resolve("auto","zh-CN")=="zh" && Text.Resolve("auto","zh-TW")=="zh","Chinese host cultures select Chinese");
            Check(Text.Resolve("auto","de-DE")=="en" && Text.Resolve("auto",null)=="en","other host cultures fall back to English");
            Check(Text.Resolve("en","zh-CN")=="en" && Text.Resolve("zh","en-US")=="zh","explicit language overrides host");
            Text.SetLanguage("auto"); Text.SetHostLanguage("zh-CN");
            Check(Text.T("Exposure must be finite and positive")=="曝光时间必须为有限正值","auto observes host language");
            Text.SetHostLanguage("de-DE");
            Check(Text.T("Exposure must be finite and positive")=="Exposure must be finite and positive","auto switches to English fallback");
            foreach(var lang in new[]{"en","zh"}) {
                Text.SetLanguage(lang);
                Check(Text.F(FormattableStringFactory.Create("{1} / {0} / {1}","A","B"))=="B / A / B","positional arguments can repeat and reorder: "+lang);
                Check(Text.F(FormattableStringFactory.Create("{{{0,4:X4}}}",0x14c))=="{014C}","escaped braces, alignment and format: "+lang);
                var caps=new ExposureCapabilities(false,1,900,.001,1800,3600,false,false);
                try { ExposurePolicy.Create(.1,new[]{.01},caps); throw new Exception("expected unsupported exposure"); }
                catch(NotSupportedException ex) {Check(ex.Message.StartsWith(lang=="zh"?"曝光 ":"Exposure ") && ex.Message.Contains("Bulb"),"actual unsupported-exposure error is translated: "+lang);}
                try { ExposurePolicy.Create(.1,new[]{.01},caps with{BulbAvailable=true}); throw new Exception("expected Bulb bounds rejection"); }
                catch(ArgumentOutOfRangeException ex) {Check(ex.Message.StartsWith(lang=="zh"?"软件 Bulb":"Software Bulb"),"actual Bulb bounds error is translated: "+lang);}
                try {CameraSupport.Select(Array.Empty<CameraDescription>(),null);throw new Exception("expected camera selection rejection");}
                catch(InvalidOperationException ex) {Check(ex.Message.StartsWith(lang=="zh"?"期望恰好一台":"Expected exactly one"),"actual camera selection error is translated: "+lang);}
            }
            using var stream=typeof(Text).Assembly.GetManifestResourceStream("SonyCamera.Client.Texts.json")!;
            using var json=JsonDocument.Parse(stream);
            foreach(var entry in json.RootElement.EnumerateObject()) {
                if(!entry.Name.Contains('{'))continue;
                var format=CompositeFormat.Parse(entry.Name);
                foreach(var lang in new[]{"zh","en"}) {
                    var translated=CompositeFormat.Parse(entry.Value.GetProperty(lang).GetString()!);
                    Check(translated.MinimumArgumentCount==format.MinimumArgumentCount,"translation argument count: "+entry.Name+" / "+lang);
                }
            }
        } finally {Text.SetHostLanguage(CultureInfo.CurrentUICulture.Name);Text.SetLanguage(savedLanguage);}

        var monitor=new CropMonitor();
        var full=CropMonitor.FromBody("ILCE-7M5",1);
        var crop=CropMonitor.FromBody("ILCE-7M5",2);
        var raw=new RawInfo {Model="ILCE-7M5",Width=7028,Height=4688};
        Check(!monitor.ObserveBody(full),"initial full frame does not warn");
        var mismatch=CropMonitor.VerifyRaw(full,"ILCE-7M5",raw with{Width=4640,Height=3088},1);
        Check(mismatch.State==SensorFrame.Unknown && monitor.ObserveRaw(mismatch),"body/RAW disagreement warns once and reports unknown");
        for(var i=0;i<12;i++) {
            Check(!monitor.ObserveBody(full) && monitor.Current.State==SensorFrame.Unknown,"same body refresh cannot clear RAW contradiction");
            Check(!monitor.ObserveRaw(mismatch),"persistent RAW mismatch does not repeat notifications");
        }
        Check(!monitor.ObserveRaw(CropMonitor.VerifyRaw(full,"ILCE-7M5",raw,5)) && monitor.Current.State==SensorFrame.Unknown,"reduced RAW without evidence cannot clear contradiction");
        Check(monitor.ObserveRaw(CropMonitor.VerifyRaw(monitor.Body,"ILCE-7M5",raw,1)) && monitor.Current.State==SensorFrame.FullFrame,"new consistent RAW resolves contradiction");
        Check(!monitor.ObserveBody(full) && monitor.Current.RawVerified,"body refresh preserves verified evidence");
        Check(monitor.ObserveBody(crop) && monitor.Current.State==SensorFrame.ApsC,"actual body mode change warns");
        Check(!monitor.ObserveRaw(mismatch) && monitor.Current.State==SensorFrame.ApsC,"late frame from previous body mode cannot overwrite current status");
        Check(!monitor.ObserveBody(crop),"unchanged crop does not warn again");
        monitor.Reset();Check(monitor.ObserveBody(crop),"first crop after new connection warns once");
        Check(!monitor.ObserveBody(crop),"first crop warning is deduplicated");
        var unknown=CropMonitor.FromBody("ILCE-7M5",3);
        Check(unknown.State==SensorFrame.Unknown && CropMonitor.VerifyRaw(unknown,"ILCE-7M5",raw,1).State==SensorFrame.Unknown,"Auto body mode cannot be inferred from dimensions alone");
        Check(CropMonitor.FromBody("unknown model",1).State==SensorFrame.Unknown,"unknown model does not borrow full-frame identity");
        Check(CropMonitor.VerifyRaw(full,"ILCE-7M5",raw with{Model="ILCE-7RM6"},1).State==SensorFrame.Unknown,"RAW must belong to connected model");
        Check(CropMonitor.VerifyRaw(crop,"ILCE-7M5",raw with{Width=4640,Height=3088},1).RawVerified,"model-specific APS-C RAW corroborates body mode");
    }
}
