using NINA.Core.Utility;
namespace SonyA7V.Camera;

public sealed partial class SonyCameraService {
    private volatile string? currentOperation;
    private bool announceOperation;
    public string? CurrentOperation => currentOperation;

    private async Task EnterOperationAsync(string name, CancellationToken token = default, bool announce = true) {
        await opGate.WaitAsync(token).ConfigureAwait(false);
        currentOperation = name;
        announceOperation = announce;
        if (announce) NotifyChanged();
    }

    private void ExitOperation() {
        var announce = announceOperation;
        currentOperation = null;
        opGate.Release();
        if (announce) NotifyChanged();
    }

    private async Task<T> CoordinateAsync<T>(string name, Func<Task<T>> action, CancellationToken token = default, bool announce = true) {
        await EnterOperationAsync(name, token, announce).ConfigureAwait(false);
        try {return await action().ConfigureAwait(false);}
        finally {ExitOperation();}
    }

    private Task CoordinateAsync(string name, Func<Task> action, CancellationToken token = default) =>
        CoordinateAsync(name, async () => {await action().ConfigureAwait(false); return true;}, token);

    // Observers cannot interrupt SDK operations or prevent another view receiving its state.
    private void NotifyChanged() {
        foreach (Action observer in Changed?.GetInvocationList() ?? Array.Empty<Delegate>()) {
            try {observer();}
            catch (Exception ex) {Logger.Error("[SonyCrSDK] Session observer failed: " + ex.Message, ex);}
        }
    }
}
