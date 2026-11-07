# Ary's Launcher

作者 Aeracy。

这是《少女の永恒地牢》整合包使用的 Windows x64 启动器。支持 Microsoft 正版登录、LittleSkin 和自定义 Yggdrasil 认证，可导入 Forge 整合包，管理 Java、内存、下载和更新。

启动器源码在本仓库维护。ArysMod 的构建产物随 Release 发布，Mod 源码由作者在独立仓库维护。

## 使用

下载 Release 中的安装程序，或解压便携包。启动器随 Windows 显示语言使用简体中文或英文。首页的「安装整合包」会选中随附的官方包，确认后下载清单中的依赖。也可以选择本地的 .mrpack 或 ZIP。安装完成后，在账号页登录并启动游戏。

Arys 整合包带有 Mod 配置和资源包加载顺序。发行时跳过存档、备份、服务器列表和个人设置，首次启动前清理残留头像与开场播放标记。

新装使用清理过测试玩家数据的内置地图。整合包更新会保留同一版本的已安装 ArysMod，避免仅因归档校验值变化而重新生成已有维度。Mod 升级时按新版本替换。

游戏文件保存在启动器自己的 `.minecraft` 中，账号与设置保存在 `data`。Microsoft 授权在内嵌的官方网页中完成。

新安装首次启动时全屏运行，使用主屏幕当前的分辨率和刷新率。之后在 Minecraft 内修改画面设置，启动器沿用游戏保存的结果。已有实例的显示设置也会保留。

整合包卡片右下角可检查更新，发现文件差异后下载并应用。更换 Mod 版本时，启动器通过 Mod ID 识别旧 JAR，并在安装新版前移走旧文件。文件校验和恢复记录用于处理下载失败或应用中断。

登录账号保存在本机 `data/accounts.json`，令牌通过 Windows DPAPI 绑定当前 Windows 用户加密。发布附件会检查账号文件、浏览器缓存和疑似令牌。

Microsoft 账号优先显示 Xbox 档案头像，读取失败时尝试 Minecraft 皮肤头像。LittleSkin 和其他外置认证使用角色皮肤的脸部与帽子层。头像缓存在本机。窗口背景加入星雾、星轨与微光，窗口和 exe 使用 Mod 的紫色眼睛图标。

背景星点按三层景深漂移并独立消退重生，流星通常间隔 4–7 秒随机出现。页面滚轮使用逐帧缓动，动画与滚动交由系统合成渲染节奏驱动。失去焦点或最小化时暂停背景动画。

## 构建

安装 .NET 10 SDK，在 PowerShell 7 中运行。

```powershell
.\Build.ps1
.\Build.ps1 -Package
.\Build.ps1 -Package -BundledPack .\artifacts\release-pack-v1.2.0\Arys-Modpack.mrpack
```

打包需要 NSIS 3.13，可放入 `.tools/nsis-3.13` 或通过 `NSIS_HOME` 指定目录。构建脚本获取并校验微软的离线 WebView2 组件，使用单节点编译，输出位于 `app` 和 `artifacts`。

主程序是包含 .NET 运行时的压缩 exe。安装程序优先复用系统 WebView2，缺少时保留离线组件，在首次登录时解压。

轻量便携包使用系统 WebView2。需要离线备用组件时，使用完整安装程序。

## 发布更新

`release-channel.json` 保存公开仓库、两个 tag 前缀和发行公钥。`v*` 发布启动器，例如 `v1.0.6`；`pack-v*` 发布整合包，例如 `pack-v1.2.0`。Mod、配置和资源包的改动都通过整合包 tag 发布。启动器分别查找各序列中带签名清单的最高版本，不依赖仓库统一的 Latest 标记。

每个 Release 只携带所属更新清单。文件路径、大小及 SHA-256 写入清单，并使用 ECDSA P-256 签名。整合包发布不会改变 GitHub 为启动器保留的 Latest 标记。

```powershell
.\New-UpdateRelease.ps1 -Kind Launcher -Version 1.0.6
.\Publish-Pack.ps1 -Version 1.2.1 -InstanceDirectory D:\Pack
.\Publish-Pack.ps1 -Version 1.2.1 -InstanceDirectory D:\Pack -NotesFile .\changes.md -Publish
```

发行私钥由作者保存在仓库外，默认路径为 `.tools/signing/updates-private.pem`。请保留可恢复的备份，后续版本使用同一把密钥签名。将生成的清单、签名和文件上传至同一 Release，附件名称保持生成时的名称。

整合包工具需要 Python 3.12 或以上版本。它默认复用上一版公开包的下载索引，按 SHA-1、SHA-512 和文件大小核对下载项。CurseForge 文件变动后，可用 `-Template` 指向更新过的 .mrpack。新配置的归属登记在 `src/ArysLauncher/pack-content-policy.json`。`-NormalizeLocal` 会整理本机 Mod 文件名，将同内容的副本和已卸载 Mod 的配置移入实例的 `.arysmod-backups`。加 `-Publish` 才会上传，工具从 Git Credential Manager 获取当前 GitHub 登录。

整合包更新按发行清单比较文件，并识别同 Mod 的旧 JAR。替换前核对现有文件，准备完整后写入恢复记录，再应用变更。新增文件与旧文件的移除可跨版本执行。配置默认保留本地内容，其他文件发生冲突时会指出位置。下载缓存按 1 GiB 上限清理。

启动器更新由副进程等待主程序退出后应用，完成后重新打开。网络失败可重试，应用文件时中断会在下次打开时恢复。

第三方组件的许可证保存在 `docs`，随二进制分发至 `Licenses`。
