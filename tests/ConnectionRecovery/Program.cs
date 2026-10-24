using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using SonyA7V.Camera;
using SonyCamera.Client;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Supply the NINA app directory; prepare an isolated TEST manifest beside this executable first.");
        var host = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(host, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        await Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var manifest = Path.Combine(AppContext.BaseDirectory, "native", "approved-native.json");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));
        // TEST packages are explicitly allowed only in this isolated test process.
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest).TrimStart('\uFEFF'));
        Check(document.RootElement.GetProperty("backend").GetString() == "test", "Refuse a real SDK package");
        var client = new SonyCameraClient(new NativeDeploymentOptions(manifest, hash, AllowTestPackage: true));
        var service = new SonyCameraService();
        typeof(SonyCameraService).GetField("client", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, client);
        var saved = Environment.GetEnvironmentVariable("SW_FAKE_CASE");
        var savedModel = Environment.GetEnvironmentVariable("SW_FAKE_MODEL_A7V");
        try
        {
            await client.InitializeAsync();
            Environment.SetEnvironmentVariable("SW_FAKE_MODEL_A7V", "1");
            foreach (var scenario in new[] { "feature-not-manual", "feature-af-blocks" })
            {
                Environment.SetEnvironmentVariable("SW_FAKE_CASE", scenario);
                try { await service.ConnectAsync(null!, CancellationToken.None); throw new Exception("Readiness failure was skipped"); }
                catch (CameraException ex) { Check(ex.Message.Contains(scenario == "feature-not-manual" ? "manual exposure" : "focus", StringComparison.OrdinalIgnoreCase), "Preserve the original readiness error: " + ex.Message); }
                Check(!service.Connected && service.Facts is null, "Failed connection must not appear ready");
                Check(client.State == SessionState.Disconnected, "Release partial SDK connection");
            }
            Environment.SetEnvironmentVariable("SW_FAKE_CASE", "feature-normal");
            await service.ConnectAsync(null!, CancellationToken.None);
            Check(service.Connected && service.Facts is not null, "Retry succeeds in the SAME process/client");
            await service.DisconnectAsync();
            Check(!service.Connected && service.Facts is null, "Disconnect clears readiness facts");
            await service.ConnectAsync(null!, CancellationToken.None);
            Check(service.Connected && service.Facts is not null, "Reconnect reloads facts");
            Console.WriteLine("PASS: two readiness failures release the session; same-client retry, disconnect and reconnect succeed.");
        }
        finally
        {
            await service.ShutdownAsync();
            Environment.SetEnvironmentVariable("SW_FAKE_CASE", saved);
            Environment.SetEnvironmentVariable("SW_FAKE_MODEL_A7V", savedModel);
        }
    }
}
