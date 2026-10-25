# 架构

| 层 | 源码 | 职责 |
|---|---|---|
| 原生包装 | `src/native` | C++ C ABI 封装 SDK、状态与回调；Sony 类型不跨边界 |
| 通用客户端 | `src/managed` | P/Invoke、唯一 owner 线程、命令队列、取消与状态；不依赖 NINA/WPF |
| NINA 适配 | `src/nina` | 相机接口、解码桥、Sony 控制页和可选镜头对焦器 |

相机、控制页与镜头对焦器使用 `SonyCameraService.Shared`，在同一服务协调连接、参数、预览、曝光提交和交付。完整曝光等待不占协调锁，取消仍可进入；完成后重新核对会话及拍摄身份。独立工具复用通用客户端，不与 NINA 跨进程共享句柄。

Core ABI 2 与 Feature ABI 1 的布局、PE 架构、依赖哈希和测试后端隔离在加载时检查。LibRaw 固定源码与构建方法见 [构建说明](BUILD.md)；项目桥接仅交付当前照片的有效 Bayer 样本。

界面示意和架构图见 [项目首页](../README.md)。用户操作见 [使用说明](USAGE.md)。
