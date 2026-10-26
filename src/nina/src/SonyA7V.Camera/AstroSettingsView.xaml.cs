using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace SonyA7V.Camera;
public partial class AstroSettingsView : UserControl {
    private readonly IAstroSettingsSession session;
    private readonly PluginSettingsStore store;
    private AstroSettingsViewModel? model;
    public AstroSettingsView():this(SonyCameraService.Shared,new PluginSettingsStore(PluginStorage.Current.SettingsDirectory())){}
    public AstroSettingsView(IAstroSettingsSession session,PluginSettingsStore store) {
        this.session=session;this.store=store;DataContext=null;InitializeComponent();SettingsWheelRouting.Attach(this);
        Loaded+=(_,_)=>{if(model is null){model=new(session,store,Dispatcher);DataContext=model;}else model.Resume();};
        // Docking/tab changes may temporarily unload the same view; retain unsaved preferences.
        Unloaded+=(_,_)=>model?.Suspend();
    }
}
public static class AstroSettingsWindow {
    private static Window? current;
    public static void Show(SonyCameraService session) {
        var dispatcher=Application.Current?.Dispatcher??System.Windows.Threading.Dispatcher.CurrentDispatcher;
        dispatcher.Invoke(()=>{
            var host=Application.Current?.MainWindow;
            if(ReferenceEquals(session,SonyCameraService.Shared)&&host is not null&&
               AstroDockViewModel.RegisteredContentId is {} contentId&&SonyDockNavigation.TryActivate(host,contentId))return;
            if(current is not null){current.Activate();return;}
            current=new Window{Title=Text.T("Sony CrSDK · 天文参数与状态"),Width=980,Height=Math.Min(820,SystemParameters.WorkArea.Height-40),MinWidth=740,MinHeight=500,Content=new AstroSettingsView(session,new PluginSettingsStore(PluginStorage.Current.SettingsDirectory())),WindowStartupLocation=WindowStartupLocation.CenterOwner};
            if(Application.Current?.MainWindow is {} owner&&owner.IsVisible)current.Owner=owner;
            current.Closed+=(_,_)=>current=null;current.Show();
        });
    }
}
