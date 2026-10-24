using System.ComponentModel.Composition;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.ManifestDefinition;
using System.IO;
using NINA.Plugin.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace SonyA7V.Camera;

/// <summary>
/// Manifest values are supplied by assembly attributes (Guid, AssemblyTitle, FileVersion,
/// AssemblyMetadata), which is how NINA 3.2's PluginBase resolves IPluginManifest.
/// </summary>
[Export(typeof(IPluginManifest))]
public sealed class SonyA7VPlugin : PluginBase, IPluginManifest
{
    // Installed plugin cards use a local image so they work without GitHub access.
    // Reimplement the interface property because PluginBase.Descriptions is not virtual.
    public new IPluginDescription Descriptions {
        get {
            var description = base.Descriptions;
            var icon = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location)!, "icon.png");
            return new PluginDescription {
                ShortDescription = description.ShortDescription,
                LongDescription = description.LongDescription,
                FeaturedImageURL = File.Exists(icon) ? new Uri(icon).AbsoluteUri : description.FeaturedImageURL
            };
        }
    }

    private readonly IImageDataFactory factory;
    private readonly IImagingMediator imaging;
    private readonly IImageSaveMediator saving;
    private readonly IProfileService profiles;

    [ImportingConstructor]
    public SonyA7VPlugin(IImageDataFactory factory, IImagingMediator imaging, IImageSaveMediator saving, IProfileService profiles)
    {
        this.factory = factory; this.imaging = imaging; this.saving = saving; this.profiles = profiles;
    }

    public override Task Initialize()
    {
        Logger.Info($"[SonyA7V] Plugin initialized {Version}; host={CoreUtil.Version}; framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        AutoTest.ScheduleIfRequested(factory, imaging, saving, profiles);
        return Task.CompletedTask;
    }

    public override async Task Teardown()
    {
        await SonyCameraService.Shared.ShutdownAsync().ConfigureAwait(false);
        Logger.Info("[SonyA7V] Plugin teardown complete");
    }
}

[Export(typeof(IEquipmentProvider<ICamera>))]
[Export(typeof(IEquipmentProvider))]
public sealed class SonyA7VEquipmentProvider : IEquipmentProvider<ICamera>, IEquipmentProvider
{
    private readonly object gate = new();
    private SonyA7VCamera? camera;

    [ImportingConstructor]
    public SonyA7VEquipmentProvider(IImageDataFactory factory, IProfileService profiles) { Factory = factory; this.profiles = profiles; }

    private readonly IProfileService profiles;

    private IImageDataFactory Factory { get; }

    public string Name => "Sony A7 V";

    public IList<ICamera> GetEquipment()
    {
        lock (gate) camera ??= new SonyA7VCamera(SonyCameraService.Shared, Factory, profiles);
        return new List<ICamera> { camera };
    }
}

