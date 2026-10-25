# 天文设置与镜头对焦器：0.5.4-rc.1

程序集 0.5.4.0；SDK 2.02.00；固定 LibRaw 0.22.0-Release。机型及验证范围见 [验证说明](VALIDATION.md)，界面使用见 [Sony 控制面板](NINA_ASTRO_UI.md)。

## 能力审计与实现

| 能力 | 天文用途 | 当前结果 |
|---|---|---|
| 精确普通快门、ISO、软件 Bulb | Light/Flat/Dark 序列 | 已有；优先真实普通快门候选，包括相机实际提供的长快门；其余再检查 Bulb |
| 完整 RAW 与压缩候选 | 星点、校准与叠加 | 真实 M5/R6 18 文件验证，R6 活动区域和新机 TIFF 截断检查已修复 |
| 长曝 NR、机身防抖、手动镜头对焦、无损 RAW | 避免额外等待、镜组移动与格式混用 | 新增语义准备 API，默认不改设置，先全部预检再写入回读 |
| 高 ISO NR | 明确的可选机内设置 | 可选封装；不假设对 RAW 一定有效 |
| 电量百分比 | 夜间供电观察 | 通用客户端已有读取，本轮接入 NINA；未知不显示成 0 或 100 |
| 光圈 | 镜头收光圈改善星点 | 通用精确候选写入，本轮增加 NINA 明确动作 |
| 镜头绝对位置、驱动状态、相对脉冲 | 镜头对焦与以后自动对焦验收 | 新增驱动状态和完整位置/静止回读；NINA IFocuser 默认关闭，需同一相机会话 |
| LiveView、放大、显示效果 | 找星和手动辅助对焦 | 预览已有；0.5.3-dev 已取消机身对焦放大 UI，图像缩放使用 NINA 电脑预览；不当作传感器 RAW；曝光前停止 |
| 存储、卡余量、原始文件目录/下载 | 原片备份与排障 | 已有内容接口；新增存储目的地属性。内容连接与拍摄连接分开，不能盲目补传不明归属照片 |
| 机内录像 | 后续行星/预览工作流 | 下两层已有明确开始/停止和回读，不新增 NINA 深空录像流程 |
| 实时传感器温度、制冷、硬件 ROI/Binning | 暗场匹配/专用天文相机能力 | 不伪造。SDK 的过热关机阈值不是温度；已有可选外部 ExifTool 事后元数据读取，不冒充实时温度 |

属性支持来自本机官方 SDK 2.02 属性表和头文件，再由运行时可读/可写/候选值核验；不向发布包复制私有 SDK 文档或头文件。

## 第一层与第二层

原生 C ABI 保持 core ABI 2 与 Feature ABI 1 的已有结构布局，新增向后兼容导出 `sw_get_astro_target`。
语义编号 1~5 分别为长曝 NR Off、高 ISO NR Off、SteadyShot Off、MF、全尺寸无损 RAW 压缩档。SDK token 在原生层转换。
无损档优先当前已为 Lossless/Lossless L 的值，否则选择实际候选；不拿 S/M 或有损格式替代。这里“全尺寸”针对压缩尺寸档，**不会替用户关闭 APS-C 裁切**。

通用属性新增 ID 46 `FocusDrivingStatus`、47 `StillStorageDestination`，合计 47 项。`Api_InvalidCalled` 保留错误，不能当作“不支持”来掩盖当前模式或暂时故障。

C# `PrepareAstroSessionAsync` 在同一个原生所有者命令内预检所有请求，避免被拍摄命令插入；不重复写相同值。回执逐项记录 Before/Requested/Submitted/Confirmed。失败可能已产生部分改动，因此不盲目回滚；保留回执并在未确认写入后隔离会话。
`RestoreAstroSessionAsync` 仅接受同一干净连接的回执；先核对所有当前值和原值候选，再反向恢复。相机/用户后来改过值，或发生重连，就拒绝覆盖。连接初始化中途失败的回执也会保留并写日志。

## NINA 设置文件

`settings.json` 与插件 DLL 同目录，重连时读取。所有新选项默认不启用。望远镜直焦无需 `manualLensFocus` 或镜头对焦器。

```json
{
  "saveOriginalRaw": "also",
  "rawCopyFolder": "raw",
  "liveViewColor": false,
  "cameraModel": "ILCE-7M5",
  "enableLensFocuser": false,
  "astroPreparation": {
    "disableLongExposureNoiseReduction": true,
    "disableSteadyShot": true,
    "fullSizeLosslessRaw": true
  }
}
```

使用电子镜头时可额外设 `manualLensFocus: true`；是否关闭高 ISO NR 另行决定。若请求的属性缺失、只读或没有目标候选，连接会明确失败；不要把所有选项盲目复制到每台相机。机身 M、仅 RAW、Single 等其余前置条件仍须满足。
设置不会在断开时自动恢复，避免无人值守结束后盲目写入；如需恢复，在同一空闲连接显式执行恢复动作。重连后旧回执失效，原设置仍以相机当前读回为准。

## NINA 明确动作

`ICamera.SupportedActions` 暴露下列名称；调用接口为 `ICamera.Action(name, parameters)`，不是 SDK 任意命令透传，参数面板也通过共享服务调用这些能力。

| 名称 | 参数与返回 |
|---|---|
| `Sony.GetAstroStatus` | 参数空；返回型号、连接、几何、电量、曝光能力和准备回执 JSON；未知像元/温度为 null |
| `Sony.PrepareAstroSession` | 参数为上述 astroPreparation JSON 对象；只在空闲且预览关闭时写入；未知 JSON 字段会报错 |
| `Sony.RestoreAstroSession` | 参数空；恢复当前连接保存的准备回执；不覆盖后来改动 |
| `Sony.SetAperture` | 参数为英文小数格式 f-number，如 `4`；必须是精确候选，不把 3.5 圆整到 4 |

存在已提交的准备回执时不能用新计划覆盖它，须先恢复或另开连接。通用 C# 客户端不依赖 NINA，可独立调用同样能力。

## 可选镜头对焦器

设置 `enableLensFocuser: true`，重连相机，再在 NINA 对焦器选择 `Sony CrSDK Lens Focus (experimental)`。它共享已连接的相机，不创建第二个 SDK 句柄，断开对焦器不会断开拍照相机。
必须有电子镜头、MF、可写绝对位置范围、实际位置以及驱动状态回读。望远镜/无镜头组合不虚构对焦器。未知物理步长与温度为 NaN；位置是 SDK 单位，不是微米或 ASCOM 电机实步数。

移动完成须同时确认目标位置和 NotDriving。位置属性是最近确认的位置；在机身/镜头上手动改焦后应重新连接对焦器以刷新。相机断开/重连后对焦器不会继承旧位置，必须重新连接。
Sony 文档提示环境、近远端等会影响返回焦点的一致性，自动对焦前必须用星点验收。`Halt` 无法证明停止一个绝对位置请求，因此移动中明确报不支持，原操作继续有界等待确认；取消也不会谎称机械镜组已停止。底层相对脉冲的 stop 不是绝对移动的已验证取消。默认关闭，应先验证小幅移动和星点效果，再评估自动对焦用途。

## 可复现的离线验证

```powershell
.\build.ps1 -Native -NinaApp '<NINA目录>'
python tools/Fetch-RealRawCorpus.py artifacts/astro-20261025/samples artifacts/astro-20261025/verified-corpus.json --download
python tests/test_real_raw.py . artifacts/astro-20261025/verified-corpus.json artifacts/astro-20261025/real-raw-result.json
.\tools\Test-MultiCamera.ps1 -NinaApp '<NINA目录>' -RealRawIndex artifacts/astro-20261025/verified-corpus.json
```

下载原片需要显式 `--download`；不带时仅验证已有文件。输入任一缺失或哈希不符就拒绝“部分成功”。像素指纹是固定版本回归基线，不是独立解码算法的正确性证明。

部署须使用完整新候选：wrapper、通用客户端、NINA 插件、LibRaw 与桥接器匹配。保持 SDK/source lock、清单哈希与 ABI 检查，不覆盖旧批准文件来凑加载。候选不包含 Sony SDK 或真实照片。
