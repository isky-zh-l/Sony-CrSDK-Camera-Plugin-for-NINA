# 前两模块功能扩展与第三模块接入说明（2026-10-24）

本轮完成常用天文拍摄所需的一批 SDK 封装、C# 调用接口和桌面验证入口。状态为“已编译并通过离线替身测试，新增能力待实机验收”。不能把它写成“所有 SDK 功能实机可用”或“已完成 NINA 整机验收”。

## 功能核对与完成情况

| 功能 | 此前状态 | 本轮结果与边界 |
|---|---|---|
| 枚举、连接、断开、参数读取 | 已有 | 保留，新增 Contents 连接模式；模式切换须断开、枚举、重连 |
| ISO、普通曝光、B 门、ARW 单张与连续序列 | 已有 | 保留；回归确认不由新增接口绕过拍摄隔离 |
| 无电子镜头、望远镜直焦 | 旧代码要求有效光圈 | 修正：光圈未知返回 NaN，允许拍照；ISO/快门等必要检查仍保留 |
| LiveView | 缺失 | SDK 开关设置回读；逐帧复制 JPEG，尺寸查询、容量变化、无帧、启停、旧帧保护；C# 按需拉帧，不自动创建无界队列 |
| 光圈与相机设置 | 仅基础曝光 | 新增 45 项属性的读取、候选或范围、可写状态；允许写的属性使用实际候选并回读 |
| 电量、卡状态、剩余张数/时间、录像状态 | 缺失 | 新增读取；电量非百分比/特殊值不转换为虚构百分比 |
| 对焦 | 缺失 | 绝对位置设置后读实际位置；相对速度脉冲带有界停止与失败隔离。电子镜头支持为前提，不代表毫米距离 |
| 机内录像 | 缺失 | 显式开始/停止，读取 RecordingState 确认；停止失败/超时保留不确定状态；不使用盲目 Toggle |
| 卡内原文件/原视频 | 缺失 | 独立 Contents 模式，日期目录、文件枚举、原大小、原尺寸文件下载、进度、取消/超时；完成须同时满足对应通知、大小一致、完整顺序读取 |
| 温度 | 无实时 SDK 数值 | 提供拍后 ARW CameraTemperature 读取工具方法；用户显式指定外部 ExifTool，缺失标签返回 null；不是温度实时遥测 |
| 桌面验证工具 | 基础曝光界面 | 新增参数、预览/录像、卡内文件、拍后温度四个页面；扩展窗口使用同一 C# Client |

已参考旧 Sony NINA 插件的相机与镜头对焦实现，以及 NINA 的 ICamera/DSLR 接口。旧 Sony 插件有 JPEG 预览、ISO/电量和镜头 focuser；它的相机温度实现返回 NaN，没有可作为标准直接复用的完整录像接口。NINA 的相机契约有 Start/Download/StopLiveView，并不因此具备机内录像存取 API。

参考来源：

- [旧 Sony 插件](https://github.com/dougforpres/NINASonyCameraPlugin)、[相机驱动](https://github.com/dougforpres/NINASonyCameraPlugin/blob/master/Drivers/CameraDriver.cs)、[镜头对焦驱动](https://github.com/dougforpres/NINASonyCameraPlugin/blob/master/Drivers/FocuserDriver.cs)。研究用于功能对照，没有把旧驱动代码复制为新 SDK 实现。
- [NINA ICamera 研究快照对应提交](https://github.com/isbeorn/nina/blob/2393eae581145ed5b8114bf07c48ca2580540fd5/NINA.Equipment/Interfaces/ICamera.cs)。本机已有第三模块来源快照；实际插件仍应以选定 3.2 宿主程序集核对。
- [NINA NikonCamera](https://github.com/isbeorn/nina/blob/develop/NINA.Equipment/Equipment/MyCamera/NikonCamera.cs)，用于功能对照，不作为 NINA 3.2 二进制兼容证明。
- Sony 官方 CrSDK 2.02 本机 API Reference：function_list/device_property_list.html、control_code_list.html、api_list.html；api_reference/liveview.html、connection.html、device_setting.html；ContentsTransferMode、getLiveViewAndOSD 示例。官方包由用户自行获得，源码交付不附带包或整份文档。
- [ExifTool Sony 标签说明](https://exiftool.org/TagNames/Sony.html)。能否从特定 A7 V 文件读出温度，仍须用实际文件与具体工具版本验证。

## 接口边界

继续使用 C++ C ABI → P/Invoke → 通用 C# Client → NINA 薄适配层，不改用 C++/CLI。

- 原 Core ABI 2 的 26 个入口及七个结构尺寸保留；增加 13 个入口和独立 Feature ABI 1，大小 56/24/272/584/1240。C 头文件编译检查与运行时 C#/原生尺寸核对已通过。
- 新入口：include/sony_features.h；实现：src/sony_features.inc，由原生包装编译单元使用同一互斥门和异常边界编译。新头文件不含 SDK 头文件。
- C#：FeatureModels.cs、Native.Features.cs、SonyCameraClient.Features.cs、ArwMetadata.cs。核心库不依赖 WinForms、NINA 或测试应用。
- 属性 ID 是本项目的稳定 ID，不是 SDK 属性码。候选值是相机回报的整数选项，不应跨连接、机型或 SDK 版本持久化后盲写。SDK 属性枚举、指针与结构没有穿过 ABI。
- 光圈方便接口用 f 数；通用光圈选项为 f×100。曝光秒数继续使用原来的物理单位接口。其余通用数值的含义按属性定义，不把所有整数都当物理量。
- 回调只写原生有界邮箱，按连接对象、文件句柄及目标文件关联。C# 不接收 SDK 回调函数指针；所有原生调用仍在同一 owner thread。
- LiveView JPEG 返回独立 byte[]；大尺寸 ARW/视频保留文件路径与字节数，避免把大视频塞入单个 byte[]。
- 设置后失败、录像不确定、下载取消/超时会阻止后续写入/任务。下载取消并不宣称相机传输已停；必须干净断开。旧曝光恢复仍遵守原拍摄任务的 reconcile 协议。
- 一个进程继续仅有一个 Client 生命周期；重连复用 Client，不通过 Dispose 后再次创建来绕过资源规则。原生库按现有规则固定至进程退出。

## 第三模块可以怎样接

1. 先 CheckFeatureAbiAsync；不含扩展入口的旧原生包会明确报告 NotSupportedException。没有静默把无预览说成有预览。
2. CanShowLiveView 依据实际探测。StartLiveView → StartLiveViewAsync；DownloadLiveView → GetLiveViewFrameAsync 后由 NINA 图像工厂解码；StopLiveView → StopLiveViewAsync。null 是当前无帧，应等待或取消。预览 JPEG 转 Gray16 不会增加原始有效位深。
3. 进入 RAW 拍照前先停预览，处理或停止录像；再走原 CaptureAsync/DownloadExposure 链路。保持 NINA UI/序列的取消语义和状态通知。
4. 电量用 GetBatteryAsync；温度仅用同一张成功 ARW 的元数据结果。返回未知，不把 AutoPowerOffTemperature 当温度计。实际 RAW 尺寸、位深、CFA/裁切区域仍须由 ARW 解码器核对，不能从 JPEG 预览推断。
5. 对焦适配属于单独的 NINA IFocuser/插件 UI 工作，通用 C# 层已有控制入口；不把 IFocuser 引入原生库。
6. 机内录像/视频文件下载没有普通 ICamera 的直接对应方法。第三模块需另做 UI 或序列指令，不能声称实现 LiveView 即等于实现行星录像。这里没有 SER/AVI 编码器，也没有把预览帧冒充原视频。
7. 文件模式：StopMovie、StopLiveView → Disconnect → Enumerate → Connect(index,Contents) → GetFolders/GetContents → DownloadContent → Disconnect → Enumerate → Connect(index,Remote)。同一场景多相机必须显式核对设备，桌面工作台要求仅一台相机。
8. 本轮不替换第三模块正在使用的原生批准清单；集成时必须显式使用新包装库、新 Client 和新包指纹，并在 NINA 宿主重新做加载/CrAdapter 部署验收。

## 有意不伪装成支持的能力

- A7 V 没有该 SDK 提供的实时传感器数值温度、制冷设定/功率、除露加热、天文 CMOS 式 offset/USB 带宽控制；相应 NINA capability 应为 false/未知。
- 硬件 binning、传感器 RAW ROI 没有由这批 SDK 封装获得。软件裁切/binning 属于图像层，不能回报为相机硬件能力。
- 没有电子镜头时无法控制镜头光圈/位置；普通相机拍照仍允许未知光圈。
- 本轮未加入自动重连、多相机、RAW 连拍任务关联、所有 SDK 摄像机机型、RTSP、固件升级、格式化或删除卡内文件。它们不是本轮常用天文相机封装的隐含完成项。
- 原 Core ABI 2 曝光政策仍为 M 档、RAW only、Single，普通快门≤30秒，软件 B 门 1..900 秒（毫秒请求粒度不代表真实传感器毫秒精度）。这不是 SDK/机身的全部物理上限。扩展设置可读写某些其他相机模式，不等于原 RAW 拍摄任务支持这些模式。

## 构建、部署与回退

C++ 仍通过 build.ps1 -SdkRoot <合法外部 SimpleCli> 构建。Feature 版默认本次单独使用 build-features。C# 用 build.ps1 构建，SDK 不参与 C# 编译。

新桌面入口是仓库根目录 run-desktop-features.cmd，它从自身目录推导工具/包位置，读取包内相对 features.json。旧 run-desktop.cmd 和旧原生批准记录保留，以便对照或回退。

新机准备：先编译两层，再用 tools/prepare-feature-desktop.ps1 -WrapperDll <已审核包装DLL> -SdkRuntime <合法SDK运行目录>。源构建产生新指纹时应先完成维护者审核、更新独立批准记录，不能关闭校验自动批准任意 DLL。包目录有清单后拒绝原地覆盖，换新版本目录。

deployment/approved-native-features.json 只代表本地已审阅源代码并做离线验证的构建，不表示 Sony 签名、再分发许可或实机验收。deployment/approved-native-builds.json 保留旧验收记录。本地 artifacts 包含合法 SDK 运行依赖，禁止随源代码快照上传；Git ignore 与本次源码归档均排除了这些文件。

## 实机验收顺序（本轮没有执行）

1. 新进程连接 A7 V，读出完整能力清单；有镜头和望远镜/无电子镜头分别记录可用项。确认特殊电量值处理。
2. LiveView 开启、连续取帧、调整参数/对焦、停止；停止后拒绝旧帧；重新启动及断线收尾。
3. 回归普通曝光、B 门、取消、重复拍摄；ARW 与卡内文件按大小核对并用实际解码器解码。无镜头场景不得因光圈未知被拒绝。
4. 逐项验证实际有用的候选参数和回读；优先光圈/对焦、RAW 压缩、降噪、静音/快门、图像尺寸。
5. 短时间机内录像、确认录像状态、停止；切文件模式取回指定原视频，与卡内字节数比对并实际播放/解码，核对视频编码/分辨率/帧率。
6. 用当前版本 ExifTool 读取实际 A7 V ARW；有标签则记录拍后温度，未识别则留未知。
7. 上述结果连同第三模块的 NINA 曝光、LiveView、存盘测试通过后，才对新增功能做实机验收结论。旧版已验收 ARW 不能替代新版验收。

## 45 项扩展属性

下表全部在用户本机官方 CrSDK 2.02 的 ILCE-7M5 列有支持标记。支持标记不保证当前模式或镜头允许写；运行时探测是准据。

| 项目 ID | C# 属性 | 包装层允许尝试写入 |
|---|---|---|
| 1 | Aperture | 是，仍需相机确认可写及候选值 |
| 2 | FocusMode | 是，仍需相机确认可写及候选值 |
| 3 | ExposureMode | 是，仍需相机确认可写及候选值 |
| 4 | DriveMode | 是，仍需相机确认可写及候选值 |
| 5 | FileFormat | 是，仍需相机确认可写及候选值 |
| 6 | RawCompression | 是，仍需相机确认可写及候选值 |
| 7 | ImageSize | 是，仍需相机确认可写及候选值 |
| 8 | AspectRatio | 是，仍需相机确认可写及候选值 |
| 9 | LongExposureNoiseReduction | 是，仍需相机确认可写及候选值 |
| 10 | HighIsoNoiseReduction | 是，仍需相机确认可写及候选值 |
| 11 | SilentMode | 是，仍需相机确认可写及候选值 |
| 12 | ShutterType | 是，仍需相机确认可写及候选值 |
| 13 | LiveViewQuality | 是，仍需相机确认可写及候选值 |
| 14 | LiveViewDisplayEffect | 是，仍需相机确认可写及候选值 |
| 15 | LiveViewStatus | 否，仅查询 |
| 16 | BatteryPercent | 否，仅查询 |
| 17 | BatteryLevel | 否，仅查询 |
| 18 | RecordingState | 否，仅查询 |
| 19 | Slot1Status | 否，仅查询 |
| 20 | Slot2Status | 否，仅查询 |
| 21 | Slot1RemainingImages | 否，仅查询 |
| 22 | Slot2RemainingImages | 否，仅查询 |
| 23 | Slot1RemainingSeconds | 否，仅查询 |
| 24 | Slot2RemainingSeconds | 否，仅查询 |
| 25 | FocusPosition | 是，仍需相机确认可写及候选值 |
| 26 | FocusPositionActual | 否，仅查询 |
| 27 | FocusMagnifier | 是，仍需相机确认可写及候选值 |
| 28 | FocusMagnificationTime | 是，仍需相机确认可写及候选值 |
| 29 | InitialFocusMagnifier | 是，仍需相机确认可写及候选值 |
| 30 | MovieFormat | 是，仍需相机确认可写及候选值 |
| 31 | MovieRecordingSetting | 是，仍需相机确认可写及候选值 |
| 32 | MovieFrameRate | 是，仍需相机确认可写及候选值 |
| 33 | WhiteBalance | 是，仍需相机确认可写及候选值 |
| 34 | ColorTemperature | 是，仍需相机确认可写及候选值 |
| 35 | ExposureCompensation | 是，仍需相机确认可写及候选值 |
| 36 | MeteringMode | 是，仍需相机确认可写及候选值 |
| 37 | FocusArea | 是，仍需相机确认可写及候选值 |
| 38 | SteadyShot | 是，仍需相机确认可写及候选值 |
| 39 | AutoPowerOffTemperature | 是，仍需相机确认可写及候选值 |
| 40 | ContentsTransferStatus | 否，仅查询 |
| 41 | ContentsTransferProgress | 否，仅查询 |
| 42 | RemoteSaveImageSize | 是，仍需相机确认可写及候选值 |
| 43 | FocusIndicator | 否，仅查询 |
| 44 | ApsCMode | 是，仍需相机确认可写及候选值 |
| 45 | MovieRecordingMedia | 是，仍需相机确认可写及候选值 |

完整映射保存在 feature-property-support.json；SDK 属性码仅存在 C++ 内部。
