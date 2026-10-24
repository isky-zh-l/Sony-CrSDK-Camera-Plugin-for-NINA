namespace SonyCamera.Desktop;

internal static class Program {
    [STAThread]
    static void Main(string[] args) {
        ApplicationConfiguration.Initialize();
        try { Application.Run(new MainForm(DesktopOptions.Parse(args))); }
        catch(Exception ex) {
            Environment.ExitCode=1;
            Console.Error.WriteLine(ex);
            try {var options=DesktopOptions.Parse(args);Directory.CreateDirectory(options.LogDirectory);File.WriteAllText(Path.Combine(options.LogDirectory,"desktop-startup-error.txt"),ex.ToString());}catch{}
            if(!args.Contains("--verify-ui")&&!args.Contains("--smoke-ui"))MessageBox.Show("桌面应用启动失败："+ex.Message,"Sony 相机测试",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
    }
}
