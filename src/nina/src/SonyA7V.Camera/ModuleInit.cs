using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace SonyA7V.Camera;

/// <summary>
/// NINA loads plugins from its plugin directory, but the default load context probes the
/// application directory first. This module initializer makes the plugin directory (and its
/// native subdirectory) resolvable before any plugin type is used, so SonyCamera.Client.dll
/// travels with the plugin instead of being dropped into the NINA installation.
/// </summary>
internal static class ModuleInit
{
    private static int installed;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) != 0) return;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var root = Path.GetDirectoryName(typeof(ModuleInit).Assembly.Location);
            if (string.IsNullOrEmpty(root)) return null;
            var candidate = Path.Combine(root, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
        try { NativeLibrary.SetDllImportResolver(typeof(ModuleInit).Assembly, ResolveNative); }
        catch (InvalidOperationException) { /* another resolver already installed; keep default probing */ }
    }

    private static IntPtr ResolveNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var root = Path.GetDirectoryName(typeof(ModuleInit).Assembly.Location);
        if (string.IsNullOrEmpty(root)) return IntPtr.Zero;
        foreach (var candidate in new[] { Path.Combine(root, "native", libraryName + ".dll"), Path.Combine(root, libraryName + ".dll") })
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        return IntPtr.Zero;
    }
}
