using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Windows.Media;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;

namespace SonyA7V.Camera;

/// <summary>NINA imaging-workspace entry. It exposes the same session as the camera and setup views;
/// it never owns or initializes a second SDK client.</summary>
[Export(typeof(IDockableVM))]
[PartCreationPolicy(CreationPolicy.Shared)]
[Guid("39f42352-4fba-4533-9cba-5203a5e76cf8")]
public sealed class AstroDockViewModel : DockableVM {
    public static string? RegisteredContentId {get; private set;}
    public IAstroSettingsSession Session => SonyCameraService.Shared;

    [ImportingConstructor]
    public AstroDockViewModel(IProfileService profiles) : base(profiles) {
        RegisteredContentId = ContentId;
        Title = Text.T("Sony CrSDK 控制");
        Text.Changed+=()=>{var dispatcher=System.Windows.Application.Current?.Dispatcher;if(dispatcher is null)Title=Text.T("Sony CrSDK 控制");else dispatcher.BeginInvoke(new Action(()=>Title=Text.T("Sony CrSDK 控制")));};
        CanClose = true;
        HasSettings = false;
        ImageGeometry = new GeometryGroup();
        ImageGeometry.Children.Add(Geometry.Parse("M2,5 L7,5 L9,2 L15,2 L17,5 L22,5 L22,19 L2,19 Z M8,12 A4,4 0 1 0 16,12 A4,4 0 1 0 8,12"));
        // NINA composes plugin dock models on a worker thread; the docking UI binds on its dispatcher.
        ImageGeometry.Freeze();
    }
}
