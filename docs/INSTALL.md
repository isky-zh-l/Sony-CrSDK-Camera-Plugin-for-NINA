# 安装 Sony CrSDK Camera Plugin for N.I.N.A.

作者与维护者：isky。当前候选二进制版本 0.5.4-rc.1（程序集 0.5.4.0），目标 N.I.N.A. 3.2.0.9001、Windows x64。

其他型号配置见 [机型与 RAW](MULTI_CAMERA.md)。必须升级完整匹配包，不能仅替换托管 DLL。

## 普通用户安装（无需编译）

1. 安装目标版本 N.I.N.A.，记下包含 `NINA.exe` 的目录。目标系统还需 Microsoft Visual C++ v14 x64 运行库，可从 [微软官方页面](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) 获取；本包不捆绑或自动安装系统运行库。
2. 从项目 GitHub Releases 获取 **Sony-CrSDK-Camera-Plugin-0.5.4-rc.1.zip**。不要使用 GitHub 自动生成的 Source code ZIP 作为安装包。
3. 自行通过 [Sony 官方许可与下载入口](https://support.d-imaging.sony.co.jp/app/sdk/licenseagreement_d/en.html) 获取 Camera Remote SDK 2.02.00 Windows x64。脚本不会替你接受许可、下载 SDK 或安装驱动。按官方指引配置 PC Remote USB 及驱动。
4. 解压 SDK 内的 SimpleCli 示例；保留 `SimpleCli/external/crsdk/` 下完整运行库及 `CrAdapter/` 子目录。安装仅需运行库，源码开发才需要 `app/CRSDK` 下的头文件。不要从不明来源下载单个 DLL 拼凑。
5. 解压插件候选包到独立目录（不要直接解压进 NINA），完全退出目标 N.I.N.A.。
6. 在包目录打开 PowerShell，运行：

   ```powershell
   .\Install.ps1 -SonySdkRoot 'D:\SDK\SimpleCli' -NinaApp 'D:\Apps\NINA\app'
   ```

   也可右键 `Install.ps1` 使用 PowerShell 运行，按提示输入两个目录。默认插件位置是当前用户的 `%LOCALAPPDATA%\NINA\Plugins\3.0.0\SonyA7V.Camera`。脚本未签名；若系统策略阻止运行，核对包来源与 SHA256 后，可仅对本次进程执行：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -SonySdkRoot '<SimpleCli目录>' -NinaApp '<NINA目录>'
   ```

   如果 NINA 安装在 Program Files 并提示无写入权限，使用同一用户的管理员 PowerShell 重新运行。不要因此关闭杀毒或修改全局执行策略。

安装器会校验公开包和固定版本 SDK，从用户本地复制所需运行库，准备正确的相对目录和批准哈希。已有用户设置会保留。若宿主已有不同版本的共享 CrAdapter，会停止提示，不自动覆盖。

安装前只校验、不写文件：

```powershell
.\Install.ps1 -SonySdkRoot '<SimpleCli目录>' -NinaApp '<NINA目录>' -ValidateOnly
```

安装后的完整目录含你自行取得的 Sony 运行库，不要重新打包公开分发。公开 ZIP 本身不含 Sony SDK，也不含 NINA 宿主、驱动或测试照片。

## 使用与配置

打开 NINA，选择 Sony CrSDK Camera。机身使用 M（手动曝光）、MF（手动对焦）、仅 RAW、单张拍摄；本版本 A7M5 固件 2.00 已验证 Compressed、Lossless、Compressed HQ，其他型号待实测。按官方说明准备其他相机条件。

在 NINA 中设置保存路径、FITS/XISF 格式并开启保存。先拍一张短曝光，再开始序列。

`settings.json` 位于上述插件目录，默认：

```json
{"saveOriginalRaw":"never","rawCopyFolder":"raw","liveViewColor":true}
```

默认只保留 NINA 输出图像，不额外复制 ARW；它不会清理卡内照片或 SDK 临时接收目录。改为 `also` 可额外保留 ARW。`liveViewColor=false` 改为灰度预览。修改后断开重连相机生效，序列期间先停止拍摄。

`sony-a7v.json` 和 `native/approved-native.json` 为安装器生成的运行身份配置，不要改成跳过校验。SDK 默认临时接收位置与 NINA 最终保存位置不同。

## 备份、升级及卸载

每次安装将备份被替换的文件，默认在 `%LOCALAPPDATA%\SonyCrSDKPlugin\backups\<时间戳>`，并输出 `install-receipt.json` 的完整路径。请保留该目录。

升级前退出 NINA，再运行新包安装器；已有 settings.json 保留。卸载/恢复请退出 NINA 后运行：

```powershell
.\Uninstall.ps1 -ReceiptPath '<该次安装输出的install-receipt.json完整路径>'
```

卸载只处理该次记录的文件：恢复覆盖前的文件，移除未被修改的新增插件文件；用户配置、照片、NINA profile、设备驱动及可能被共用的新增 CrAdapter 均保留。安装后被其他程序改动的文件会提示保留，不强删。若多次升级，按安装时间从新到旧使用相应记录。

## 从源码构建

需要 .NET 8 SDK、MSVC x64、CMake、Ninja、目标 NINA，以及用户自备 SDK。

```powershell
.\build.ps1 -Native -SonySdkRoot '<官方SimpleCli目录>' -NinaApp '<NINA目录>'
```

二进制包内的 `corresponding-source.zip` 包含该次构建对应的完整源码和锁定 LibRaw 源码。仅重建私有解码器，可在 x64 MSVC 开发者 PowerShell 中解压源码并运行 `tools/Build-Decoder.ps1`，无需 Sony SDK；详见包内 `SOURCE.md`。

## 常见问题

- 列表出现 Sony CrSDK Camera，只表示驱动入口加载；点击连接后才实际枚举相机。
- 已知可修正的 M、MF 或 RAW 设置问题可保留干净连接；修正后点击重新检查或重试拍摄。真实 SDK/USB 故障仍需按提示恢复或重连。
- 图片历史为空：检查 NINA 快照保存开关和保存路径。
- SDK 哈希不符：核对官方版本；不要修改清单绕过版本检查。
- 连接失败：检查 PC Remote、USB 驱动、线缆及是否被 Imaging Edge 等其他程序占用。
- 本包为发布候选；已完成另一台 Windows 11 x64 电脑的独立部署与基本成像验证，整夜序列及其他机型仍需分别验证。详见 [验证范围](VALIDATION.md)。
