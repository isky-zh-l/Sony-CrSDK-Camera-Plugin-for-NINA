# C++ / C ABI 包装层

本模块不依赖 C# 或 NINA。公共契约为 include/sony_wrapper.h 和 include/sony_features.h；Sony SDK 类型不跨越这条边界。

推荐从仓库根运行 build.ps1 -Native。独立构建可运行本目录的 build.ps1 -SdkRoot <官方解压的SimpleCli>，需要 Windows x64、MSVC、CMake 和 Ninja。

本地 build/ 中可能包含 SDK 文件，不公开分发。test/ 的 fake_sdk.cpp 是项目自己的隔离替身，不是真实相机。

tools/test-features.ps1 -BuildDirectory <构建目录> 仅运行 fake/ 中的功能替身。其他 run-*.ps1 属于独立诊断工具；真实模式会操作相机，不由构建自动执行。NINA 集成的支持范围及最终验收见仓库根文档。
