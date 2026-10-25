# Sony CrSDK Camera Plugin for N.I.N.A.

<img src="assets/icon.png" alt="Sony CrSDK Camera Plugin 图标" width="180" />

通过 Sony Camera Remote SDK，将 Sony 相机接入 Windows 上的 N.I.N.A.，接收完整 ARW，解码为传感器 Bayer 像素，并由 N.I.N.A. 处理和保存 FITS/XISF。

作者与维护者：[isky](https://github.com/isky-zh-l)。独立社区项目，与 Sony 或 N.I.N.A. 官方无隶属关系，未获其认证或背书。

**当前候选版本：0.5.4-rc.1（程序集 0.5.4.0）。** 仅 Sony A7 V（ILCE-7M5，固件 2.00）具有本版本指定范围的实机记录。32 型号模拟测试和其他机型的离线 RAW 解码均不代表真机兼容。[版本说明](docs/RELEASE_NOTES.md) · [验证范围](docs/VALIDATION.md)

**异机部署与基本成像已验证。** 在另一台 Windows 11 x64 电脑上，ILCE-7M5 完成独立安装、重启使用、断开重连、单张及 68 张连续 1 秒 FITS 采集；本次 APS-C 模式输出为 4624×3080、16 位、RGGB。文件与已检查的头信息正常，逐张像素一致性和整夜运行不在本次异机验证结论内。

## 功能

- SDK 相机发现、精确型号选择、连接及重连；根据相机实际报告的能力提供 ISO、普通曝光、软件 Bulb 和 JPEG LiveView。
- 私有 LibRaw 解码 Sony 单平面 Bayer RAW，按每张照片更新有效尺寸及 CFA；保留解包像素，不做去马赛克、白平衡或黑电平扣除。
- N.I.N.A. 序列拍摄、FITS/XISF 保存及可选原始 ARW 副本；取消曝光，不交付归属不明或过期照片。
- Sony 控制停靠面板，包含参数、天文准备与恢复、默认设置；与标准相机入口共享同一会话。
- 已知可修正设置错误保留干净连接，修正后可重新检查；真实 SDK/USB 故障或未确认操作仍明确报错。
- 电量读取和可选实验性电子镜头对焦器；未知电量、温度或物理步长不伪造。

不支持多相机同时拍摄、硬件 binning、RAW ROI、制冷控制或 N.I.N.A. 科学视频录制。镜头对焦器的自动对焦精度尚未验收。尚未进入 N.I.N.A. 官方社区插件列表。

## 界面与架构

Sony 扩展控制面板与 N.I.N.A. 标准相机入口共享连接，参数设置和序列拍摄由同一服务协调。

![Sony CrSDK 在 N.I.N.A. 中的控制面板](docs/images/sony-controls.png)

三层结构隔离 SDK、通用客户端与宿主适配，独立工具复用通用 C# 层。

![Sony CrSDK 三层架构与共享相机会话](docs/images/sony-shared-architecture.png)

## 运行环境与安装

| 项目 | 目标与验证范围 |
|---|---|
| 系统 | Windows x64 |
| 宿主 | N.I.N.A. 3.2.0.9001 / .NET 8 |
| SDK | Sony Camera Remote SDK 2.02.00，Windows x64 |
| 本版本实测机身 | ILCE-7M5，固件 2.00，单相机 USB |
| 实测 RAW | Compressed、Lossless、Compressed HQ；具体范围见验证说明 |

1. 从 [Releases](https://github.com/isky-zh-l/Sony-CrSDK-Camera-Plugin-for-NINA/releases) 获取 `Sony-CrSDK-Camera-Plugin-0.5.4-rc.1.zip`。GitHub 自动生成的 Source code ZIP 不能作为插件安装包。RC 版本应作为预发布提供。
2. 自行通过 [Sony 官方许可与下载入口](https://support.d-imaging.sony.co.jp/app/sdk/licenseagreement_d/en.html) 获取 SDK 2.02.00，按官方说明准备 PC Remote USB 与驱动。仓库和公开插件包不包含 Sony SDK、N.I.N.A. 宿主或驱动。
3. 退出 N.I.N.A.，在解压后的插件包目录运行：

```powershell
.\Install.ps1 -SonySdkRoot '<SDK 的 SimpleCli 目录>' -NinaApp '<包含 NINA.exe 的目录>'
```

安装器校验完整包及 SDK、备份将替换的文件，并从用户本地复制运行库。升级保留用户设置和接收目录；不要只覆盖托管 DLL。完整步骤、验证模式与卸载回退见 [安装说明](docs/INSTALL.md)。安装后的目录含用户自行取得的 SDK，不应重新打包公开分发。

机身使用 M、MF、仅 RAW、Single Shooting，并遵循界面提示。在 N.I.N.A. 中选择 **Sony CrSDK Camera**，设置保存路径和格式；手动拍摄需开启保存开关。先完成一张短曝光，再开始序列。其他型号的配置与限制见 [机型与 RAW](docs/MULTI_CAMERA.md)。

## 配置与控制

插件目录中的 `settings.json` 默认如下，修改后断开重连生效：

```json
{"saveOriginalRaw":"never","rawCopyFolder":"raw","liveViewColor":true}
```

`saveOriginalRaw=also` 可额外保留 ARW，`never` 不影响 N.I.N.A. 保存 FITS/XISF，也不删除卡内或临时接收文件。`liveViewColor=false` 使用灰度预览，不改变 RAW 像素。旧 `only` 模式可能遇到宿主 ARW6 缩略图问题，不推荐使用。

- [Sony 控制面板](docs/NINA_ASTRO_UI.md)：参数读写、默认配置、停靠页及滚轮行为。
- [天文设置与实验性镜头对焦器](docs/ASTRO_FEATURES.md)：准备/恢复回执、降噪、防抖和光圈。
- [错误恢复](docs/RECOVERY.md)：保留连接、重新检查及未确认状态。

## 源码与构建

```text
N.I.N.A. 相机 / Sony 面板 / 镜头对焦器
                    ↓ 共享服务
独立 C# 工具 → 通用 C# 客户端
                    ↓ P/Invoke
              C++ C ABI → Sony CrSDK → 相机
```

`work/` 保存实际构建所需的三层源码，不是缓存。第二层不依赖 N.I.N.A.；第三层适配宿主并包含私有解码桥。[架构说明](docs/ARCHITECTURE.md)

构建需要 .NET 8 SDK、MSVC x64、CMake、Ninja、目标 N.I.N.A. 与自备 Sony SDK。在 x64 MSVC 开发者 PowerShell 中运行：

```powershell
.\build.ps1 -Native -SonySdkRoot '<SDK 的 SimpleCli 目录>' -NinaApp '<NINA 安装目录>'
```

SDK 默认位于被忽略的 `deps/sony/SimpleCli`。离线测试入口见 [验证说明](docs/VALIDATION.md)，参与开发见 [贡献说明](CONTRIBUTING.md)。构建不自动连接相机。

## 反馈与许可

通过 Issues 提供相机/固件/RAW 格式、Windows/N.I.N.A./插件/SDK 版本、复现步骤和对应日志。上传前移除私人路径、设备标识与照片隐私。新增机型以实测记录确认支持范围。

项目在需求、实现、排障、文档及视觉设计中实质性使用了 GPT 系列与 DeepSeek 等 AI 工具。维护者负责集成、测试及维护决策，AI 生成代码和模拟测试不代替实机验收。

项目自有代码采用 [MIT](LICENSE)，LibRaw 保留其自身许可；Sony SDK、N.I.N.A. 及其他依赖不由本项目重新授权。见 [第三方说明](THIRD-PARTY-NOTICES.md)。感谢 N.I.N.A.、LibRaw、Sony Camera Remote SDK 和参与测试的用户。
