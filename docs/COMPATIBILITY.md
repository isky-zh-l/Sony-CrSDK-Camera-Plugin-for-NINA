# 兼容性

0.5.6（程序集 0.5.6.0）面向 Windows x64、N.I.N.A. 3.2.0.9001 和 Sony Camera Remote SDK 2.02.00。

## 已验证机型

**Sony A7 V（ILCE-7M5），固件 2.00。** 支持三种 RAW 格式、普通曝光、软件 Bulb、取消、重连、JPEG LiveView 及相机参数控制。

RAW 输出使用 16 位容器；已验证全幅 7028×4688 和 APS-C 4624×3080 的 RGGB 输出。不同画幅和 RAW 格式的有效尺寸可能不同，校准与叠加时应分组处理。

## 其他机型与限制

`config/camera-models.json` 为 SDK 型号库存。32 型号模拟覆盖和其他机型的真实 RAW 离线解码均不代表 USB 实测兼容。只有设备提供静态 RAW、ISO、快门、主机接收等必要能力时才可接入；不能用 JPEG 或视频替代传感器 RAW。

同一进程只使用一个相机会话，不支持多机并拍。有多台相机时需通过 `cameraModel` 精确选择；两台同型号造成歧义时拒绝默认连接。

不提供硬件 binning、RAW ROI、制冷或实时传感器温度；电量未知时保持未知。真实拔插恢复、整夜运行、其他机型及自动对焦精度尚未验证。尚未进入 NINA 官方社区插件列表。

## 尺寸与像元

每张 RAW 解码后更新有效区域和 RGGB/BGGR/GRBG/GBRG 排列。使用 16 位容器保留解包数值，不表示 ADC 为 16 位；不做白平衡、去马赛克、缩放或黑电平扣除。

未知机型在首张 RAW 前可能无法提供尺寸或物理像元大小。可在 settings.json 指定 `cameraModel`、`pixelSizeMicrons`，以及同时为正的 `sensorWidth`/`sensorHeight` 作为初始化值；真实 RAW 几何优先。像元大小必须按当前机型和输出模式核实，不能从宽高推算。
