# 第三方依赖与许可

本模块遵循仓库根 [第三方说明](../../THIRD-PARTY-NOTICES.md)。项目自有 C#、XAML、C++ 解码桥与脚本采用项目 MIT 许可；第三方代码保留自身许可。

- LibRaw 固定源码保存在 third_party/LibRaw，身份由 dependencies/libraw-source-lock.json 及运行库哈希记录。源码及其 COPYRIGHT、LGPL/CDDL 文本随对应源码交付；私有动态库采用 LGPL 选项，根 tools/Build-Decoder.ps1 提供重建方法。
- SonyArw6.cpp 为动态链接 LibRaw 的项目桥接代码。替换兼容解码库时应更新经审阅的 native/identity.json，不绕过加载检查。
- N.I.N.A. 及其依赖由用户安装的宿主提供，本包不附带宿主程序集。项目目标为 net8.0-windows / Windows x64。
- Sony Camera Remote SDK、CrAdapter 与驱动由用户另行获取，公开包不包含这些文件；安装器从用户本地 SDK 目录准备固定版本依赖。

LibRaw 内部组件的版权及许可见其原始 COPYRIGHT 和源码头部；根 MIT 许可不重新授权第三方代码。
