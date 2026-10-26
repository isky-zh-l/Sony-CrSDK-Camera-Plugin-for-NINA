# Sony CrSDK Camera Plugin for N.I.N.A.

1. 按 [安装教程](../docs/INSTALL.md) 准备 NINA、VC++ 运行库和自行取得的 Sony SDK。
2. 从旧手动安装版本升级前，先运行 `Preserve-Settings.ps1` 保存设置，再卸载旧版。
3. 插件管理器安装并重启后自动查找和配置 SDK；未找到时选择一次目录。手动安装可运行 `Install.ps1`。
4. 选择 **Sony CrSDK Camera**，开启保存，先拍一张短曝光。

设置与支持范围见 [使用说明](../docs/USAGE.md) 和 [兼容性](../docs/COMPATIBILITY.md)。`corresponding-source.zip` 为对应项目及 LibRaw 源码；许可和重建方法见 `SOURCE.md`。公开包不含 Sony SDK、宿主、驱动或照片。
