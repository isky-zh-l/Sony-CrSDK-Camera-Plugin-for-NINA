# 源码构建

普通安装使用发布 ZIP，见 [安装教程](INSTALL.md)。本页面向源码维护与可复现检查。

## 完整构建

准备 .NET 8 SDK 或兼容的新 SDK、MSVC x64、CMake、Ninja、NINA 3.2.0.9001 和官方 Sony SDK 2.02.00。在 x64 MSVC 开发者 PowerShell 中运行：

```powershell
.\build.ps1 -Native -SonySdkRoot '<官方SimpleCli目录>' -NinaApp '<NINA安装目录>'
```

根脚本编译包装层、解码器、通用客户端和 NINA 适配层。SDK 默认位置为被忽略的 `deps/sony/SimpleCli`；生成物位于 `artifacts/` 或各项目 `bin/obj`，不进入源码归档。构建不自动连接相机。

## 私有解码器

`third_party/LibRaw` 保留锁定上游源码和许可，`src/nina/dependencies/libraw-source-lock.json` 核对身份。只重建 LibRaw 和桥接不需要 Sony SDK 或 .NET：

```powershell
.\tools\Build-Decoder.ps1
```

该命令需要 x64 MSVC 环境。替换兼容解码 DLL 后更新经审阅的 `identity.json`；不要关闭加载校验。

## 离线检查

```powershell
dotnet run --project .\tests\SettingsPreview\SettingsPreview.csproj -c Release '-p:NinaApp=<NINA目录>'
dotnet run --project .\tests\SettingsUi\SettingsUi.csproj -c Release '-p:NinaApp=<NINA目录>' -- '<NINA目录>' '<新建输出目录>'
.\tools\Test-MultiCamera.ps1 -NinaApp '<NINA目录>'
dotnet run --project .\tests\PluginDeployment\PluginDeployment.csproj -c Release '-p:NinaApp=<NINA目录>' -- '<NINA目录>' '<公开包解压目录>' '<官方SimpleCli目录>' '<新建输出目录>'
```

测试覆盖配置、WPF、隔离替身、解码和图像交付；真实样本来源与哈希在 `config/real-raw-corpus.json`，原片不分发。模拟结果不能替代实机兼容性验证。

部署测试只在测试进程目录和指定的新建输出目录中复制依赖，覆盖 SDK 导入、错误依赖拒绝、设置迁移及安装/升级/卸载，不加载 SDK、不操作相机。不要将测试输出作为公开资产。

## 打包与本地运行

`tools/Export-Source.ps1` 导出 Git 可见源码，`tools/Build-Package.ps1` 从完整构建结果及对应源码归档准备公开包。公开包不得混入 SDK、宿主或私人配置。

重新编译的包装层需独立审阅哈希，再使用 `tools/Prepare-LocalDeployment.ps1` 准备本地依赖。独立桌面工具如需部署，必须显式传入自己的批准清单和包 ID；仓库不附带历史本机批准记录。相机操作由使用者明确发起。
