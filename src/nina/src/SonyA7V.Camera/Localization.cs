global using Text = SonyCamera.Client.Text;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using NINA.Profile.Interfaces;
namespace SonyA7V.Camera;
public sealed class LocalizedStrings : INotifyPropertyChanged {
    public static LocalizedStrings Instance {get;}=new();
    public event PropertyChangedEventHandler? PropertyChanged;
    private LocalizedStrings(){Text.Changed+=()=>{if(Application.Current?.Dispatcher is {} dispatcher&&!dispatcher.CheckAccess())dispatcher.BeginInvoke(new Action(Refresh));else Refresh();};}
    private void Refresh()=>PropertyChanged?.Invoke(this,new("Item[]"));
    public string this[string key]=>Text.T(key);
}
public sealed class LocExtension : Binding {
    public LocExtension(string key):base("["+key+"]"){Source=LocalizedStrings.Instance;Mode=BindingMode.OneWay;}
}
internal static class HostLanguage {
    private static IProfileService? profiles;
    private static INotifyPropertyChanged? applicationSettings;
    internal static void Initialize(IProfileService? service){
        if(service is null)return;
        profiles=service;
        Refresh();
        if(service is INotifyPropertyChanged observable)observable.PropertyChanged+=(_,_)=>Refresh();
        NINA.Core.Locale.Loc.Instance.PropertyChanged+=(_,_)=>Refresh();
    }
    private static void Refresh(){
        var settings=profiles?.ActiveProfile?.ApplicationSettings;
        if(!ReferenceEquals(applicationSettings,settings)){
            if(applicationSettings is not null)applicationSettings.PropertyChanged-=SettingsChanged;
            applicationSettings=settings as INotifyPropertyChanged;
            if(applicationSettings is not null)applicationSettings.PropertyChanged+=SettingsChanged;
        }
        Text.SetHostLanguage(settings?.Language?.Name);
    }
    private static void SettingsChanged(object? sender,PropertyChangedEventArgs e)=>Refresh();
}
