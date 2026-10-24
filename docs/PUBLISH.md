# GitHub 发布及 NINA 收录

公开作者：isky。目标仓库：`isky-zh-l/Sony-CrSDK-Camera-Plugin-for-NINA`。

## 发布前

- 本机用公开候选 ZIP 和自备 SDK 按 INSTALL.md 完成安装，测试连接、普通曝光、Bulb、保存/历史及配置。
- 验证默认不额外保存 ARW，单独验证灰度预览。当前 0.4.1.2 更新了元数据与配置默认值，之前 0.4.1.1 的 60 张记录不能当成本版本全部验收。
- 按计划在第二台电脑安装，不携带私人完整 SDK 工作包作为隐式环境。
- 保留源代码快照、公开包 SHA256、测试环境与结果。当前候选不标为稳定版。

## 建立 GitHub 仓库

以自己的账号创建空的公开仓库 `Sony-CrSDK-Camera-Plugin-for-NINA`，不要再自动生成 README 或 LICENSE。本地只推送已筛选源码根，不能推送私人归档。

```powershell
git -C '<源码根>' status --short
git -C '<源码根>' add .
git -C '<源码根>' diff --cached --stat
git -C '<源码根>' commit -m "Prepare Sony CrSDK Camera Plugin release candidate"
git -C '<源码根>' branch -M main
git -C '<源码根>' remote add origin https://github.com/isky-zh-l/Sony-CrSDK-Camera-Plugin-for-NINA.git
git -C '<源码根>' push -u origin main
```

第一次提交需要 Git 配置的提交者姓名/邮箱，可使用 GitHub 提供的隐私邮箱。账号认证通过 GitHub 正常登录流程完成，不把 token、密码或邮箱凭据写进代码。

通过验证后创建对应 tag/Release，上传公开二进制 ZIP、源码 ZIP 和 SHA256SUMS。RC 版本勾选 Pre-release。不要上传带用户 SDK 的已安装目录、测试证据照片、系统 NINA 或私人归档。此文档中的仓库地址是目标地址，不表示已经创建或发布。

## NINA 社区插件库

先确认 GitHub Release 下载地址可用，再使用最终 ZIP 的校验值准备 manifest，并按 [官方仓库流程](https://github.com/isbeorn/nina.plugin.manifests) 验证 schema、提交 PR、等待维护者审阅合并。包改变后必须重算校验值。

公开 ZIP 的托管 DLL 位于根目录，采用 ARCHIVE 形式。包不含 Sony SDK；即使通过 NINA 插件页下载，用户仍需退出 NINA 后运行包内 Install.ps1，以自行取得的 SDK 完成本地配置。需要在插件介绍里明确这一额外步骤，并接受维护者对这种安装方式的评审。

AI 辅助开发及依赖许可信息保持真实披露。提交 manifest 不表示已经被收录；收录也不构成 Sony 或 NINA 对本项目的官方背书。
