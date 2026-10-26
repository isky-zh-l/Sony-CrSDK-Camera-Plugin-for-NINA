# 安装与卸载

需要 Windows x64、N.I.N.A. **3.2.0.9001**、Microsoft VC++ v14 x64 运行库和 Sony Camera Remote SDK **2.02.00 Windows x64**。普通安装不需要开发工具。

## 准备依赖

- [N.I.N.A. 下载](https://nighttime-imaging.eu/download/)
- [Microsoft VC++ 运行库](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)
- [Sony SDK 许可与下载](https://support.d-imaging.sony.co.jp/app/sdk/licenseagreement_d/en.html)

Sony SDK 由用户自行取得并接受许可。解压到任意可读取的本地目录，保留 `SimpleCli/external/crsdk/` 或 `external/crsdk/` 结构，以及完整的 `CrAdapter/`；无需编译示例，也无需把 SDK 手动复制进 NINA 目录。插件不下载 SDK、不接受许可、不安装驱动；PC Remote、驱动和 USB 设置请遵循 Sony 说明。

## 从 0.5.4 或更早手动安装版本升级

首次改用插件管理器前，退出 NINA，使用新包的脚本保留旧设置：

```powershell
.\Preserve-Settings.ps1 -LegacyPluginDirectory '<旧插件目录>'
```

脚本只复制偏好和接收缓存路径到独立用户目录，不覆盖已有设置。随后在 NINA 中卸载旧版、重启，再安装新版。不要同时保留两份 Sony 插件。首次升级后重新导入官方 SDK。

## 插件管理器安装

此安装方式在插件获社区收录后可用；目前可使用下方手动安装方式。

1. 自行下载并解压指定版本的 Sony SDK。
2. 在 NINA 插件管理器中点击 **Install** 安装 **Sony CrSDK Camera Plugin for N.I.N.A.**，然后按 NINA 提示重新启动。
3. 插件启动时**只检测**已配置的 SDK，不会自动安装文件、提权或弹出安装向导；只有在 **SDK 配置** 页显式导入时才会复制文件，若 NINA 目录受保护则此时请求管理员权限。已有不同版本的共享适配器时停止，不覆盖。
4. 未检测到 SDK 时插件不会自动弹窗。请打开插件设置 → **SDK 配置**，选择 SDK 解压目录、`SimpleCli` 或 `crsdk` 目录，点击 **导入 SDK**。此后保留该配置，无需反复选择。
5. 机身设为 M、MF、仅 RAW、Single Shooting，在 NINA 选择 **Sony CrSDK Camera**；开启保存，先拍一张短曝光。

SDK 的盘符和顶层文件夹名不限。自动查找优先使用已有配置和已保存位置，再查找下载、桌面、文档及常见 Sony/SDK 文件夹；不会无界扫描全部磁盘。放在未找到的自定义深层目录时，首次选择一次即可。也可通过环境变量 `SONY_CRSDK_ROOT` 指定位置。

配置只校验和复制文件，不连接相机。手动导入时先断开相机；当前进程已加载 SDK 时，先重启再配置。可在 **SDK 配置** 页点击 **检查配置**，或重新执行自动查找。

## 手动安装

下载对应发布页的插件 ZIP 和 `SHA256SUMS`，校验后解压到独立目录。源码 ZIP 不是安装包。

```powershell
Get-FileHash -LiteralPath '<插件ZIP>' -Algorithm SHA256
```

将结果与 `SHA256SUMS` 中的同名文件核对。退出 NINA，在包目录运行：

```powershell
.\Install.ps1 -SonySdkRoot '<SimpleCli目录>' -NinaApp '<包含NINA.exe的目录>' -ValidateOnly
.\Install.ps1 -SonySdkRoot '<SimpleCli目录>' -NinaApp '<包含NINA.exe的目录>'
```

NINA 目录受保护时需要管理员 PowerShell。安装器检查 SDK、原生架构及包完整性；不同版本的共享适配器会导致安装停止。保留安装器输出的回执，供手动卸载使用。

为什么适配器必须装在 NINA 目录：Sony Camera Remote SDK 按宿主可执行文件所在目录查找 `CrAdapter/`（USB/IP 适配器及其运行库），不会从插件目录加载。因此安装器把 `Cr_PTP_USB.dll`、`Cr_PTP_IP.dll`、`libusb-1.0.dll`、`libssh2.dll` 写入 `NINA.exe` 同级的 `CrAdapter/`。这是 SDK 的路径约束而非插件偏好；该目录可能与其他 Sony 插件共享，安装器发现不同版本时会停止而不是覆盖，手动删除也可能影响其他插件。

安装脚本与 SDK 导入都需要用户显式触发：插件启动时只检测已配置的 SDK，不安装文件、不提权，也不弹出安装向导。

## 设置与升级

偏好、SDK 缓存和接收路径保存在 `%LOCALAPPDATA%/NINA/PluginData/9c1f3b7e-5a24-4f0c-9a86-2d1e7c4b6a51/`，不放在会被升级替换的插件目录里。

后续升级保留上述目录。不同构建使用独立运行时快照，依赖不匹配时停止并提示重新配置。SDK 与接收目录不会自动同步到其他电脑。

## 卸载

- 插件管理器安装：在 NINA 中卸载，然后重启。
- 手动安装：退出 NINA，运行 `Uninstall.ps1 -ReceiptPath '<安装回执>'`。

卸载保留个人设置和 SDK 缓存，便于重装。确认不再需要时可自行删除上述用户数据目录。共享的 `CrAdapter` 不由插件管理器卸载；其他插件可能仍在使用它。

## 常见问题

| 现象 | 处理 |
|---|---|
| 未自动找到 SDK | 在配置页选择已解压的 SDK、SimpleCli 或 crsdk 目录并导入 |
| SDK 哈希或架构不符 | 重新取得完整匹配的官方包，不修改校验清单 |
| 已有不同版本的共享适配器 | 核对其他插件依赖后处理，不直接覆盖 |
| 已取消管理员授权或目录访问被拒绝 | 在 SDK 配置页重试并选择管理员权限，或手动安装时使用管理员 PowerShell |
| 插件重复 | 保留一份安装；迁移前先保存设置并卸载旧版 |
| 没有图像文件 | 确认 NINA 的保存开关与输出目录 |

参数和支持范围见 [使用说明](USAGE.md) 和 [兼容性](COMPATIBILITY.md)。
