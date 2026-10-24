namespace SonyCamera.Desktop;

internal sealed record DesktopOptions(string ConfigPath, string LogDirectory, string CaptureDirectory, string SettingsDirectory, bool Smoke, bool Verify, bool TestOnly) {
    public static DesktopOptions Parse(string[] args) {
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SonyNinaCrSdk");
        string? data=null, config=null;
        bool smoke=false, verify=false, test=false;
        for(int i=0;i<args.Length;i++) {
            string Next(){if(++i>=args.Length)throw new ArgumentException($"{args[i-1]} 缺少路径。");return Path.GetFullPath(args[i]);}
            switch(args[i]) {
                case "--config":config=Next();break;
                case "--data-dir":data=Next();break;
                case "--smoke-ui":smoke=true;break;
                case "--verify-ui":verify=true;break;
                case "--test-only":test=true;break;
                default:throw new ArgumentException($"未知参数：{args[i]}");
            }
        }
        if(smoke&&verify)throw new ArgumentException("--smoke-ui 和 --verify-ui 不能同时使用。");
        string settings=data==null?root:Path.Combine(data,"settings");
        string pictures=Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if(string.IsNullOrWhiteSpace(pictures))pictures=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if(string.IsNullOrWhiteSpace(pictures))pictures=root;
        return new(config??Path.Combine(settings,"client.json"),Path.Combine(data??root,"logs"),data==null?Path.Combine(pictures,"SonyNinaCrSdk"):Path.Combine(data,"captures"),settings,smoke,verify,test);
    }
}
