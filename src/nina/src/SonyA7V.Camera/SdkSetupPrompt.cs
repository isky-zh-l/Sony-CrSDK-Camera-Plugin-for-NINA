using System.Windows;
using System.Windows.Threading;

namespace SonyA7V.Camera;

internal static class SdkSetupPrompt {
    private static Window? window;
    internal static void Schedule(SdkSetupResult result) {
        var app=Application.Current;if(app is null)return;
        app.Dispatcher.BeginInvoke(new Action(()=> {
            var owner=app.MainWindow;if(owner is null)return;
            void Show() {
                if(window is not null){window.Activate();return;}
                var view=new SdkSetupView(PluginStorage.Current);view.SetInitialResult(result);
                window=new Window{Title="Sony CrSDK · SDK 配置",Content=view,Width=780,Height=470,MinWidth=650,MinHeight=440,Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15,0x1b,0x24)),WindowStartupLocation=WindowStartupLocation.CenterOwner};
                if(owner.IsVisible)window.Owner=owner;
                window.Closed+=(_,_)=>window=null;window.Show();
            }
            if(owner.IsLoaded)Show();else {RoutedEventHandler? loaded=null;loaded=(_,_)=>{owner.Loaded-=loaded;Show();};owner.Loaded+=loaded;}
        }),DispatcherPriority.ContextIdle);
    }
}
