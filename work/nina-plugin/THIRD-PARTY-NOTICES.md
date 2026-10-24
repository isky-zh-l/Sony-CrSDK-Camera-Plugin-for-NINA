# 第三方依赖与许可

- 本项目新编写的 C#/XAML/C++ 桥接、验证启动辅助库和脚本：MIT，见 LICENSE。没有复制 Sony MTP 插件源码。
- LibRaw：固定源码提交 `2caab57e269cc7fce17c3189e4d4d7e3e5861875`，源码 [固定归档](https://github.com/LibRaw/LibRaw/archive/2caab57e269cc7fce17c3189e4d4d7e3e5861875.zip)。运行时版本字符串仍为 `0.22.0-Release`，不能据此当成发行版 0.22.0 或已失败的 0.22.2；以提交、source-lock 和 DLL SHA256 为身份。本阶段复用既有验收的 MSVC x64 动态 DLL；默认 Makefile.msvc，未启用 RawSpeed、DNG SDK、JPEG、LCMS 或额外 demosaic packs。桥接为 `/MD /O2 /EHsc /std:c++17 /LD`，没有修改 LibRaw。
- 本次选择 LibRaw 的 LGPL-2.1 许可选项，随包保留 licenses/LICENSE.LGPL、COPYRIGHT，也附原始 CDDL 选项。允许用户替换兼容的动态库及为修改调试进行逆向工程，不施加额外限制。identity.json 是可编辑的本地文件完整性记录，替换自己构建的兼容 DLL 时应更新其哈希；它不是 Sony 原生批准清单。完整源码须从固定归档或合法本地快照单独取得，按 README 重建；项目目录不混入源码缓存。公开分发二进制前，应随分发渠道提供完整对应 LibRaw 源码及所需许可文本，不能仅以本阶段的开发机缓存替代源码交付。本阶段为本地开发验证，不发布在线安装包。
- LibRaw 内含 dcraw、DCB/FBDD、X3F、Adobe DNG 等代码的版权说明见原始 COPYRIGHT 和对应源码头部；ARW6 文件另声明 Copyright 2026 LibRaw LLC、Jiang Yaogeng。固定源码文件清单见 dependencies/libraw-source-lock.json。动态 DLL 的源文件未作修改。
- NINA 3.2.0.9001：MPL-2.0；本插件只引用用户安装中的同版本程序集，不附带 NINA 核心 DLL、不重建/修改宿主。NINA 图像处理器依赖由宿主提供。主程序、Accord、Nito、MEF、System.Text.Json 等均由原宿主提供，未打入插件目录。
- Microsoft .NET 8 / WPF、VC++ x64 runtime：依赖宿主/系统合法安装；本工程未升级或安装运行库。构建可使用已安装 .NET SDK 10，但产物目标固定 net8.0-windows；不是 .NET 10 插件。
- 无 Sony SDK、CrAdapter、SonyCamera.Client、sony_wrapper 依赖；本阶段交付包没有 Sony 头文件、lib、DLL、测试照片或宿主应用副本。
