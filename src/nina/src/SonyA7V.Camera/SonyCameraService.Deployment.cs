namespace SonyA7V.Camera;

public sealed partial class SonyCameraService {
    public async Task ConfigureDeploymentAsync(Func<Task> configure) {
        ArgumentNullException.ThrowIfNull(configure);
        await EnterOperationAsync(Text.T("配置 SDK"));
        try {
            if (Connected || client is not null) throw new InvalidOperationException(Text.T("请重新启动 NINA，在连接相机前配置 SDK。"));
            await configure();
        } finally { ExitOperation(); NotifyChanged(); }
    }
}
