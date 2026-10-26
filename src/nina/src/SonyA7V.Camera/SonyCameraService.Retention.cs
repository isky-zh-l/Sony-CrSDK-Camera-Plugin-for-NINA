using System.IO;
using NINA.Core.Utility;
using SonyCamera.Client;
namespace SonyA7V.Camera;
public sealed partial class SonyCameraService {
    // The loader owns its pixel/RAW byte arrays before this is called. Retain failures
    // (including failed original-copy operations) for explicit user cleanup only.
    public void CompleteDelivery(CaptureSnapshot capture) {
        if(!capture.IsSuccess || deployment is null)return;
        try {
            var root=Path.GetFullPath(deployment.ReceiveRoot).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            var directory=Path.GetFullPath(capture.ReceiveDirectory);
            var file=Path.GetFullPath(capture.ArwPath);
            if(!directory.StartsWith(root,StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(Path.GetDirectoryName(directory),root.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase) ||
               !Path.GetFileName(directory).StartsWith("capture-",StringComparison.Ordinal) ||
               !string.Equals(Path.GetDirectoryName(file),directory,StringComparison.OrdinalIgnoreCase))return;
            PluginStorage.NoLinks(directory);PluginStorage.NoLinks(file);
            if(File.Exists(file))File.Delete(file);
            if(Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory);
        }catch(Exception ex){Logger.Warning("[SonyCrSDK] Delivered RAW cleanup failed; retained for manual removal: "+ex.Message);}
    }
}
