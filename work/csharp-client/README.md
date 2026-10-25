# 通用 C# 相机客户端

SonyCamera.Client 通过 P/Invoke 使用项目自己的 C ABI。它管理单 owner 线程、异步操作、等待、取消与状态，不依赖 NINA。

SonyCamera.Desktop、SonyCamera.Acceptance 和 SonyCamera.Features 共用此客户端。构建入口为 build.ps1 -SkipDeploy；原生运行依赖必须另行显式准备。

deployment/ 的批准清单是先前构建的身份记录，不会自动批准重新编译的 DLL。NINA 当前本地部署请使用根 tools/Prepare-LocalDeployment.ps1。桌面工具和真实验收模式可能操作相机，构建不会自动运行。

FEATURES.md 说明底层能力和边界；当前 NINA 插件功能与验证范围以根 README 和 docs/VALIDATION.md 为准。
