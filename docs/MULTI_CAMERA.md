# 机型与 RAW：0.5.4-rc.1

程序集 0.5.4.0，SDK 2.02.00。按运行时能力适配；仅 A7M5 固件 2.00 有本版本指定范围的实机记录。32 型号模拟结果不代表真机兼容。参见 [验证范围](VALIDATION.md)、[天文功能](ASTRO_FEATURES.md) 和 [版本说明](RELEASE_NOTES.md)。

## 设备范围与边界

型号清单来自本机 SDK 2.02 的 CrCameraDeviceModelList，并与
[Sony 官方 SDK 设备列表](https://support.d-imaging.sony.co.jp/app/sdk/en/) 对照（2026-10-25）。
`config/camera-models.json` 是包的设备清单；连接不再依赖单一型号白名单，实际 SDK 属性决定能力。

| 设备组 | SDK 型号 | 本实现的处理 |
|---|---|---|
| α 全画幅 | ILCE-1、1M2、9M2、9M3、7C、7CM2、7CR、7M4、7M5、7RM4、7RM4A、7RM5、7RM6、7SM3 | 通用静态 RAW 路径，逐项检查当前模式的能力；仅 A7M5 有指定范围实机记录 |
| α APS-C | ILCE-6700 | 通用路径；不复用全画幅尺寸 |
| 工业与紧凑机 | ILX-LR1、DSC-RX0M2、DSC-RX1RM3 | 通用路径；旧机可选属性缺失不再直接阻断 |
| ZV | ZV-E1、ZV-E10M2 | 按当前静态 RAW 属性检查，不根据系列名称推断能力 |
| FX 混合机型 | ILME-FX2、FX3、FX3A、FX30 | 仅在相机实际提供静态 RAW、ISO、快门、主机接收等必要能力时接入 |
| 专业视频及云台设备 | ILME-FX6、ILME-FR7、MPC-2610、PXW-Z200、Z300、Z380、HXR-NX800、BRC-AM7 | 允许发现并检查；没有静态 RAW 能力时明确拒绝。不能将视频或 JPEG 冒充 NINA 传感器 RAW |

SDK 的传输方式与各型号固件支持范围以其官方 API 文档为准，本次没有新增手工 IP 配对界面，
没有承诺所有接口、固件或专业视频控制功能可用于 NINA。保持单会话，不支持多相机同时拍摄。

## 已实现

- NINA 列表显示通用 Sony CrSDK Camera，连接后显示 SDK 返回的型号。保留程序集、GUID、旧设备 ID，避免破坏 A7 V 的既有配置引用。
- 单机直接选择；有多台时必须使用唯一的 `cameraModel` 精确选择。不按枚举顺序默认连接第一台；两台同型号仍拒绝歧义。
- ISO、普通快门和 Bulb 由当前相机候选值决定。普通快门精确匹配优先，可超过 30 秒；不匹配时仅在相机报告 Bulb 且符合项目时限时使用软件 Bulb，允许小于 30 秒的 Bulb。
- 软件 Bulb 的 1–900 秒、1 毫秒步进是本项目的策略上限，不是 Sony 硬件极限。取消、释放重试、隔离和原值恢复保留。
- Bulb Timer 明确不存在时支持旧机型；存在但不可读、SDK 暂时错误不当作关闭。只读且读回 Off 无需写入；读回 On 时必须允许写入 Off，并在结束时恢复。
- 传输尺寸、远程保存尺寸和录像状态等可选属性仅在明确缺失时放行；存在且取值不正确、不可读或查询错误时继续拒绝。M、RAW only、Single、S2 就绪与主机保存等必要条件仍必须证明。
- 支持 SDK 已定义的 Compressed、Uncompressed、Lossless、Lossless S/M/L、Compressed HQ 格式标识；未识别的新格式拒绝。缺少压缩选择器时记为 Unknown，不能据此宣称文件有效。
- 解码桥允许锁定 LibRaw 能处理的 Sony 单平面标准 Bayer RAW，取消仅型号 7M5 和仅 ARW6 解码器名称的限制；没有 JPEG、RGB 或视频替代路径。
- 从每张 RAW 的有效区域读取宽高与 RGGB/BGGR/GRBG/GBRG，更新 NINA 属性和元数据。不做白平衡、去马赛克、缩放或黑电平减法。使用 16 位容器，实际 RAW 位数保留在 RawInfo。
- 检查下载字节数、RAW 内型号与所连相机型号、活动区域边界、像素范围和缓冲区长度。拒绝非 Sony、非标准 Bayer、损坏文件和超出资源上限的输入。
- LiveView 根据设备报告的能力显示；预览颜色选择仍独立于 RAW Bayer 排列。
- 部署清单接受非空且无重复的多型号库存，旧版单型号清单仍有效。8 项固定依赖、外部批准哈希、PE/AMD64、ABI、路径和测试后端隔离全部保留。

## 首张 RAW 前的尺寸与像元大小

SDK 当前使用的静态属性没有通用传感器尺寸和物理像元大小输出。
除历史已测 A7M5 bootstrap 外，新机在首张成功解码前尺寸返回 0（未知），随后按真实 RAW 更新。
若 NINA 工作流在首张前需要视场尺寸，可填写与当前 RAW 模式相符的 `sensorWidth` / `sensorHeight`。
二者只是 bootstrap，不会裁剪 RAW，也不会覆盖真实解码结果。

物理像元大小不能从 RAW 宽高推算。新机未知值使用 NINA 的 NaN 约定；可通过 `pixelSizeMicrons`
指定当前输出模式的有效像元大小。A7M5 的历史默认为 5.09µm；缩减 RAW 的有效像元大小不应借用全尺寸值。
未配置像元大小时，应先补齐数据再用于依赖像元大小的板解/视场计算。

```json
{
  "saveOriginalRaw": "never",
  "rawCopyFolder": "raw",
  "liveViewColor": true,
  "cameraModel": "ILCE-6700",
  "pixelSizeMicrons": 3.76
}
```

上面的像元值仅演示配置格式，实际应按机型、裁切或缩减 RAW 模式独立核实。
可选字段 `sensorWidth`、`sensorHeight` 必须同时为正整数；`bayerPattern` 只允许上述四种排列。
配置在重连时读取；默认不自动拍照来探测几何。保持 RAW only、M、MF、Single 和主机接收设置。

## 构建与离线验证

```powershell
.\build.ps1 -Native -NinaApp '<隔离 NINA 3.2.0.9001 app>'
.\tools\Test-MultiCamera.ps1 -NinaApp '<隔离 NINA 3.2.0.9001 app>'
python .\tests\test_decoder.py '<源码根目录>'
```

Test-MultiCamera 使用专用 `backend=test` 清单，必须显式 AllowTestPackage，核心仅使用假 SDK。
测试包含 32 个模拟型号、格式与能力差异、错误后的重连、未知参数拒绝、四种 Bayer，以及通过真实
NINA 图像工厂的合成 DNG 像素链路；0.5.1 又加入 18 个真实 M5/R6 ARW 的模拟传输链路与像素指纹。文件解码验证不等于固件/硬件验收。
已有原生假 SDK 回归覆盖普通捕获、错误关联、无镜头、预览、Bulb 释放与恢复。部署安全检查仅静态校验，不初始化真实 SDK。

## 交付与部署

候选包包含项目 DLL、锁定 LibRaw 及对应源码/许可、SDK 依赖哈希模板；不含 Sony SDK。按 [安装说明](INSTALL.md) 使用完整匹配包，不能只覆盖托管 DLL。保留 SDK/source lock、哈希和 ABI 检查，新二进制不能沿用旧批准。

## 尚需真机验证

新增机型逐个检查枚举/重连、ISO/快门候选、静态 RAW 传输、每种实际 RAW 模式解码和有效区域、
像元大小、Bayer、短曝光、Bulb/取消/恢复及 FITS/XISF 输出。新型号若超出当前锁定 LibRaw 的识别或解码范围，
会报错，不能仅修改型号或绕过校验作为解决方案。本版本的 NINA UI 与 A7M5 指定范围验证见 [验证说明](VALIDATION.md)；实际板解及其他机型仍需分别验收；已完成的异机部署与基本成像见验证说明。
