# 安装与卸载

适用正式版 **0.5.4-rc.1**（程序集 0.5.4.0）。普通用户使用二进制包，不需要 Visual Studio、CMake、Ninja 或 .NET SDK。

## 1. 准备环境

| 依赖 | 要求 |
|---|---|
| 系统 | Windows x64 |
| N.I.N.A. | `NINA.Core.dll` 的 FileVersion 必须为 **3.2.0.9001** |
| Sony Camera Remote SDK | **2.02.00 Windows x64** |
| 系统运行库 | Microsoft Visual C++ v14 x64 |

- 从 [N.I.N.A. 官网](https://nighttime-imaging.eu/download/) 获取匹配的安装程序，安装后核对实际文件版本。
- 从 [微软官网](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) 获取 VC++ 运行库。
- 通过 [Sony 官方许可入口](https://support.d-imaging.sony.co.jp/app/sdk/licenseagreement_d/en.html) 阅读并接受条款，取得 SDK。保留解压后的 `SimpleCli/external/crsdk/` 和完整 `CrAdapter/` 子目录；驱动及 PC Remote 设置遵循 Sony 说明。

安装器不会下载 SDK、接受许可、安装系统依赖或驱动。

## 2. 下载并校验

在 [最新正式版](https://github.com/isky-zh-l/Sony-CrSDK-Camera-Plugin-for-NINA/releases/latest) 下载：

- `Sony-CrSDK-Camera-Plugin-0.5.4-rc.1.zip`：插件安装包。
- `SHA256SUMS`：对应校验值。

GitHub 自动提供的 Source code ZIP 和单独 `-source.zip` 均为源码，不是安装包。

```powershell
Get-FileHash -LiteralPath '.\Sony-CrSDK-Camera-Plugin-0.5.4-rc.1.zip' -Algorithm SHA256
```

将结果与 SHA256SUMS 中同名文件的值对比，匹配后解压到独立目录；不要直接解压进 NINA。

## 3. 检查并安装

完全退出 NINA，在插件包目录打开 PowerShell。以下路径仅作示例，请替换为本机实际目录：

```powershell
.\Install.ps1 -SonySdkRoot 'D:\SDK\SimpleCli' -NinaApp 'D:\Apps\NINA\app' -ValidateOnly
.\Install.ps1 -SonySdkRoot 'D:\SDK\SimpleCli' -NinaApp 'D:\Apps\NINA\app'
```

先确认 `-ValidateOnly` 输出 PASS，再运行第二行。默认插件目录为 `%LOCALAPPDATA%\NINA\Plugins\3.0.0\SonyA7V.Camera`。

安装器校验包、宿主与 SDK，准备运行库及哈希清单，保留已有 `settings.json` 和接收目录，并备份被替换文件。请保留输出的 `install-receipt.json` 路径。

如果执行策略阻止已核验的脚本，可仅对本次进程使用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -SonySdkRoot '<SimpleCli目录>' -NinaApp '<NINA目录>'
```

目标目录提示权限不足时，使用同一用户的管理员 PowerShell。不要修改全局执行策略或关闭安全软件。若已有不同版本的共享 CrAdapter，安装器会停止，应先核对其他插件的依赖。

## 4. 首次拍摄

1. 机身设置 PC Remote、M 手动曝光、MF、仅 RAW、Single Shooting。
2. 启动 NINA，选择 **Sony CrSDK Camera** 并连接。
3. 选择输出目录及 FITS/XISF 格式，开启拍摄面板的保存开关。
4. 完成一张短曝光，确认新增文件可打开后再使用序列。

参数面板、LiveView 和 ARW 副本设置见 [使用说明](USAGE.md)。

## 升级、卸载和回退

升级时退出 NINA，运行同版完整安装器；不要只覆盖托管 DLL。备份默认保存在 `%LOCALAPPDATA%\SonyCrSDKPlugin\backups\<时间戳>`。

卸载或恢复某次安装，退出 NINA 后运行：

```powershell
.\Uninstall.ps1 -ReceiptPath '<该次install-receipt.json的绝对路径>'
```

脚本按回执恢复被覆盖文件、移除未修改的新增插件文件；用户设置、照片、宿主配置及可能共享的适配器保留。安装后已被改动的文件不会强删。多次升级按从新到旧顺序使用相应回执。

## 常见问题

| 现象 | 检查 |
|---|---|
| 版本或 SDK 哈希不符 | 使用指定版本和完整官方 SDK；不要修改清单绕过检查 |
| 插件入口存在但连接失败 | 检查 USB/PC Remote、驱动、线缆及其他程序是否占用相机 |
| M、MF 或 RAW 条件不符 | 按提示修正，再重新检查；真实 SDK/USB 故障按提示恢复或重连 |
| 历史列表没有图像 | 确认保存开关及输出目录，并检查本次新文件 |

安装后的目录含用户自行取得的 Sony 运行库，不应重新打包公开分发。
