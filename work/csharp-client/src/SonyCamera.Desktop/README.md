# 桌面应用源码与运行参数

此目录只负责简单的中文 WinForms 界面。所有相机业务都经过 `SonyCamera.Client.SonyCameraClient`，没有 P/Invoke 或另一条 Sony SDK 直连路线。

- `Program.cs`：启动入口和启动错误处理。
- `DesktopOptions.cs`：命令行参数与用户数据目录。
- `MainForm.cs`：日常界面、相机操作事件、错误显示和异步关闭收尾。
- `UiVerification.cs`：程序驱动真实控件的验收入口，日常界面不增加自动验收按钮。

默认配置为 `%LOCALAPPDATA%\SonyNinaCrSdk\client.json`，日志为同目录的 `logs`。照片默认为系统“图片”目录下的 `SonyNinaCrSdk`；没有图片目录时使用“文档”目录。照片目录可在界面指定，程序不删除照片。源码、EXE 和原生包目录均不保存默认用户数据。

配置使用调用层 `CameraClientConfiguration.Load`，字段为 `manifestPath`、`approvedManifestSha256`、`allowTestPackage`。相对清单路径相对于配置文件解析。桌面应用不自动计算并批准清单。普通运行禁止测试包：配置允许测试包时，仍须额外显式指定 `--test-only` 才可启用。

```text
SonyCamera.Desktop.exe --config "用户配置文件路径"
SonyCamera.Desktop.exe --smoke-ui --data-dir "可写测试数据根"
SonyCamera.Desktop.exe --verify-ui --config "已批准实机配置路径" --data-dir "可写验收数据根"
```

`--data-dir` 将日志、照片、默认配置分别放入 `logs`、`captures`、`settings`；同时传 `--config` 时使用显式配置。中文和空格路径在命令行加引号即可。旧的 `--native-dir` 已移除。

`--smoke-ui` 显示窗口约 1.2 秒，保存截图后正常关闭，不创建相机客户端、不读取或加载 SDK，无配置也可运行。

`--verify-ui` 会实际控制相机，仅由实机验证操作者独占运行。程序选择真实下拉框候选、设置数值框，然后调用按钮 `PerformClick`，沿用日常 Click 事件和 ShowError。它是程序驱动控件操作，不是人工点击测试，也不是绕过界面的调用层测试。

步骤：检查初始禁用状态 → 枚举并确认 ILCE-7M5 型号 → 连接、读回基线 → 通过控件应用 ISO/普通快门/仅电脑 → 非法目录错误显示测试（拍摄前拒绝，无新增快门）→ 两张普通顺序拍摄 → 一张 2 秒 B 门 → 一次 10 秒 B 门保持中取消 → 显式恢复 → 通过控件恢复基线 ISO/普通快门/保存位置并核对读回 → 断开。总共四次拍摄请求，预期三张成功 ARW，取消不返回成功路径。失败不补拍。`--test-only` 下型号明确标为 SIMULATED，替身结果不能当作实机证据。

基线 ISO、普通快门须在真实候选内，否则在改参、拍摄前停止。收尾状态未确认或恢复失败时停止后续控件操作，不清空隔离。关闭仍正常调用 Dispose 收尾，不强杀进程或卸载 DLL。

证据位于日志目录 `verify-ui-时间戳`：每步骤 JSON/PNG、`baseline.json`、`summary.json`，含 backend、testOnly、配置路径、清单及外部批准哈希、事件计数、禁用状态、读回参数、每张编号/路径/错误和日志路径。OperatorError 保留异常类型、原始 HResult；相机异常额外保留项目/Sony/阶段/拍摄编号。退出码 0 表示全部步骤及基线恢复通过，1 表示失败；关闭收尾失败也会置退出码 1。程序不进行 RAW 解码，成功文件仍须用独立 RAW 工具验证。
