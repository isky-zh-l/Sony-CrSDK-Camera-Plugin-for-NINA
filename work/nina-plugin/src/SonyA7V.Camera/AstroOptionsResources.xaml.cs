using System.ComponentModel.Composition;
using System.Windows;
namespace SonyA7V.Camera;
[Export(typeof(ResourceDictionary))]
public partial class AstroOptionsResources : ResourceDictionary {
    public AstroOptionsResources(){InitializeComponent();}
}
