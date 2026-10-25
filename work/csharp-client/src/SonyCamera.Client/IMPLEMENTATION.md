> 0.5.0-dev 更新：清单 supportedModels 现在是非空、无重复、最多 256 项的包设备库存，旧 ILCE-7M5 单项清单保持兼容；库存不代表真机验收。8 依赖角色、路径、哈希、AMD64 与 ABI 校验保持不变。通用曝光策略见 ExposurePolicy.cs，完整多机型限制见 docs/MULTI_CAMERA.md。下文是早期基线的历史实现记录。

# C#调用层实现说明

本目录只依赖.NET，不包含Sony SDK。`SonyCamera.Client.csproj`目标net8.0/x64，允许unsafe，仅Native.cs中的固定缓冲区及数组调用使用指针。没有UI、NINA或直接Sony SDK调用。NINA 适配层引用本程序集；硬件验证范围见根 docs/VALIDATION.md。

## 文件

- Models.cs：通用项目模型。
- Native.cs：当前权威sony_wrapper.h的26个cdecl入口和7个Pack8结构；UTF-8固定字节数组，调用方所有权；NaN原样保留，不能视为0秒。
- Deployment.cs：部署清单、包一致性只读校验及用户配置文件读写。业务运行不访问SDK原包，也不含批准哈希或开发机路径。
- NativeLoader.cs：Windows/x64限制、安全LoadLibraryEx依赖搜索、已批准包文件加载和ABI大小/偏移核验。库、只读文件锁和AddDllDirectory cookie固定到进程结束，不调用FreeLibrary。
- SonyCameraClient.cs：一个进程一个client，一个固定owner线程；队列命令+每次50ms原生wait；队列和Dispose入队共用锁；有序日志/进度队列在ThreadPool排送，避免用户事件阻塞原生owner线程。

## 启动部署

构造函数为`SonyCameraClient(NativeDeploymentOptions deployment)`。部署选项包含绝对ManifestPath、宿主从外部配置读取的ApprovedManifestSha256及AllowTestPackage。运行库中没有固定批准哈希或开发机路径，也不访问SDK原包。批准清单SHA-256必填，校验清单原始字节；不能由运行宿主从待校验清单自行计算为批准值。当前没有数字签名，哈希只证明与外部批准内容一致，不认证Sony来源。准备脚本负责人工维护的已审核源及批准流程。

`NativePackageValidator.Validate(options)`是公开只读入口，要求Windows x64进程，不加载DLL、不操作相机。报告包含PackageId、SchemaVersion、AbiVersion、Backend、ManifestPath、ManifestSha256及ValidatedFiles。校验包括：清单schemaVersion=1/ABI2、唯一supportedModels=[ILCE-7M5]、七结构大小硬门槛、8角色固定集合/精确相对路径、逐文件SHA256、原生AMD64 PE32+ DLL、禁止CLR、重复属性/未知字段/重复角色/路径逃逸/路径祖先重解析点。加载前重新哈希并持有文件只读锁，禁止之后替换依赖。ABI运行时查询和关键结构偏移检查继续保留。

清单固定角色如下；native相对清单所在目录，host相对AppContext.BaseDirectory。

| role | base | path |
|---|---|---|
|wrapper|native|sony_wrapper.dll|
|core|native|Cr_Core.dll|
|monitor|native|monitor_protocol.dll|
|monitorPf|native|monitor_protocol_pf.dll|
|usbAdapter|host|CrAdapter/Cr_PTP_USB.dll|
|ipAdapter|host|CrAdapter/Cr_PTP_IP.dll|
|libusb|host|CrAdapter/libusb-1.0.dll|
|libssh|host|CrAdapter/libssh2.dll|

每个files项包含role/base/path/sha256；必需项由客户端代码固定，不能靠删清单条目绕过检查。生产与测试都要求全集。backend只能为production/test；test还需AllowTestPackage=true，宿主负责额外的显式测试命令行开关，桌面正常运行不得开启。

`CameraClientConfiguration.Load/Save`默认读写用户LocalApplicationData/SonyNinaCrSdk/client.json，字段为manifestPath/approvedManifestSha256/allowTestPackage。Load将相对manifestPath按配置文件所在目录解析，不依赖cwd；保存只会创建用户指定配置目录，不在安装目录自动创建数据。`ToDeploymentOptions()`用于构造调用层。配置及清单可含一个UTF-8 BOM，解析前剥除；批准清单哈希仍包含BOM原字节，兼容Windows PowerShell 5.1的UTF-8写入行为。

SDK v2.02的CrAdapter要求位于**宿主EXE同级**；部署验证表明仅修改工作目录无效，将官方CrAdapter放到EXE同级后枚举成功。因此启动时验证该目录，不修改进程全局工作目录，也不自行预加载adapter。NINA 安装器同时校验宿主 CrAdapter，冲突时拒绝覆盖。

测试清单backend=test且AllowTestPackage=true才允许加载，日志明确记录TEST ONLY。同一进程不能在包之间切换。替身产生的伪TIFF绝不是RAW解码或相机验收证据。生产只承诺ILCE-7M5，未扩大到其他Sony型号。

## 状态与取消

CaptureAsync成功开始后保持同一owner轮询，同时处理CancelAsync、EndExposureAsync等命令；CancellationToken设置取消标记，owner在下一轮主动请求原生取消，最终返回原生失败快照，不仅取消等待。若token在开始之前已取消，不触发相机。

拍摄失败保持编号、主错误、释放错误、恢复错误及空ARW路径，设置captureQuarantined。ReconcileAsync有总预算，每片50ms；只有原生reconcile证明完整取回/释放/恢复且check_capture_ready成功才解除隔离。不会从目录寻找最后一张补交。

参数设置后的Verify/SDK/超时错误保守隔离，普通非法参数或前置条件错误不制造隔离。原生参数fault可由成功断开后重新枚举连接恢复；拍摄未确认不能借断开清零。正常重连请复用同一个client。

DisposeAsync主动取消活动任务，最多10秒尝试已有拍摄的显式恢复，然后尝试断开和shutdown；从不热卸载或强杀进程。拍摄仍未取得恢复证据时，保持Unconfirmed并抛出CameraException，即使SDK资源已释放也不宣称相机空闲。同步SDK调用及操作系统阻塞没有硬时间上界。

## 验证边界

客户端构建与模拟回归不构成硬件验收。当前结果及限制见根 docs/VALIDATION.md。
