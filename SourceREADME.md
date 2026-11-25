# Ary's Launcher

作者 Aeracy。

这是《少女の永恒地牢》整合包使用的 Windows x64 启动器。支持 Microsoft 正版登录、LittleSkin 和自定义 Yggdrasil 认证，可导入 Forge 整合包，管理 Java、内存、下载和更新。

启动器源码在本仓库维护。ArysMod 的构建产物随 Release 发布，Mod 源码由作者在独立仓库维护。

## 使用

下载 Release 中的安装程序，或解压便携包。启动器随 Windows 显示语言使用简体中文或英文。首页的「安装整合包」会选中随附的官方包，确认后一次准备整合包、Minecraft 和 Forge。也可以选择本地的 .mrpack 或 ZIP。全部准备完成后即可启动游戏。

账号页在安装前和下载期间都可登录。系统缺少 WebView2 时，优先使用安装程序随附的组件；便携包会从微软获取所需组件。登录窗口使用独立任务，关闭它不会取消游戏下载。

安装过程共用一条总体进度，切换阶段不再归零。游戏下载支持官方源与 BMCLAPI 自动选择和回退，文件仍按清单校验；默认并发为 16 个文件、单站点最多 8 个连接，可在设置页调整。[BMCLAPI](https://bmclapidoc.bangbang93.com/)

GitHub 更新检查和文件下载默认自动选择线路。启动器比较直连与免费中转的响应，文件下载会少量读取数据来选择线路，失败时切换并继续下载。设置页可关闭此功能。发布者仍只需上传 GitHub，无需另建下载服务器。

当前中转服务为 [GHFast](https://ghfast.top/) 和 [GH-Proxy](https://gh-proxy.com/docs/github-accelerator)。这些服务由第三方提供，速度与可用性取决于网络和服务负载。中转仅用于公开资源，账号登录不经过中转；更新清单始终验证发行签名，文件始终按原始清单校验。关闭自动选择后，Minecraft 官方源与 BMCLAPI 的原有选择规则仍保留。

Arys 整合包带有其他 Mod 的配置、光影预设和原版设置，包括默认按键、FOV 与鼠标灵敏度。首次导入时按 Windows 显示语言选择游戏语言，关闭原版初始引导与教程提示，保留 Mod 自己的开场。

1.2.2 为旧实例同步一次这套默认设置，完成后在本机记录。新安装直接记录完成。此后的更新不再覆盖、补齐或删除这些设置，也不会重设按键和 FOV。

发行包和更新清单排除 ArysMod 自身的全部配置、头像及资料缓存，ArysMod 的按键保留在原版选项里。存档、备份、服务器记录、音频设备名称和本机窗口尺寸也不会进入发行包。现有玩家的 ArysMod 配置与头像保持在本机，更新不会删除它们。

新装使用清理过测试玩家数据的内置地图。整合包更新会保留同一版本的已安装 ArysMod，避免仅因归档校验值变化而重新生成已有维度。Mod 升级时按新版本替换。

游戏文件保存在启动器自己的 `.minecraft` 中，账号与设置保存在 `data`。Microsoft 授权在内嵌的官方网页中完成。

新安装首次启动时全屏运行，使用主屏幕当前的分辨率和刷新率。之后在 Minecraft 内修改画面设置，启动器沿用游戏保存的结果。已有实例的显示设置也会保留。

整合包卡片右下角可检查更新，发现文件差异后下载并应用。更换 Mod 版本时，启动器通过 Mod ID 识别旧 JAR，并在安装新版前移走旧文件。文件校验和恢复记录用于处理下载失败或应用中断。

登录账号保存在本机 `data/accounts.json`，令牌通过 Windows DPAPI 绑定当前 Windows 用户加密。发布附件会检查账号文件、浏览器缓存和疑似令牌。

启动器显示当前账号设置的自定义头像。没有设置或暂时无法加载时，头像显示纯黑。窗口背景加入星雾、星轨与微光，窗口和 exe 使用 Mod 的紫色眼睛图标。

1.1.0 的账号页可更换头像与 Banner，选择图片后拖动调整显示范围。资料按认证来源和 UUID 分别保存，没有自定义头像时显示纯黑。游戏内同步需要配套版本的 ArysMod，旧版 Mod 仍使用游戏内的资料设置。游戏内的选图入口保留，独立启动 Mod 也可使用。图片限制沿用 Mod，高清图保留原图，启动器显示缩小预览。此版本沿用目前公开的整合包，整合包更新单独发布。

背景星点按三层景深漂移并独立消退重生，流星通常间隔 4–7 秒随机出现。页面滚轮使用逐帧缓动，动画与滚动交由系统合成渲染节奏驱动。失去焦点或最小化时暂停背景动画。

## 构建

安装 .NET 10 SDK，在 PowerShell 7 中运行。

```powershell
.\Build.ps1
.\Build.ps1 -Package
.\Build.ps1 -Package -BundledPack .\artifacts\release-pack-v1.2.2\Arys-Modpack.mrpack
```

打包需要 NSIS 3.13，可放入 `.tools/nsis-3.13` 或通过 `NSIS_HOME` 指定目录。构建脚本获取并校验微软的离线 WebView2 组件，使用单节点编译，输出位于 `app` 和 `artifacts`。

主程序是包含 .NET 运行时的压缩 exe。安装程序优先复用系统 WebView2，缺少时保留离线组件，在首次登录时解压。

便携包优先使用系统 WebView2，缺少时自动下载。需要离线备用组件时，使用完整安装程序。

## 发布更新

`release-channel.json` 保存公开仓库、两个 tag 前缀和发行公钥。`v*` 发布启动器，`pack-v*` 发布整合包。Mod、配置和资源包的改动都通过整合包 tag 发布。两个更新入口位于 `updates/launcher.json` 和 `updates/pack.json`，包含原始清单及其签名。

启动器先读取独立入口，连接失败时切换下载地址，再回退 Release 查询。启动器更新也可从 GitHub Latest 获取，整合包仍按独立 tag 选择。所有入口都验证发行签名，下载文件按清单校验。

整合包需要更高版本启动器时，界面会提示先升级启动器。两条更新检查分别处理错误，整合包检查失败不会清除已找到的启动器更新。连接失败、超时和服务限流会显示具体提示，诊断保存在本机 `data/logs/updates.log`。

每个 Release 只携带所属更新清单。文件路径、大小及 SHA-256 写入清单，并使用 ECDSA P-256 签名。整合包发布不会改变 GitHub 为启动器保留的 Latest 标记。

`Publish-GitHubRelease.ps1` 完成发布后调用 `Publish-UpdateFeed.ps1`，先核对线上清单与签名附件的哈希，再更新对应入口。入口同步失败时可单独重跑后者，不必改动已发布文件。

```powershell
.\New-UpdateRelease.ps1 -Kind Launcher -Version 1.1.0
.\Publish-Pack.ps1 -Version 1.2.2 -InstanceDirectory D:\Pack
.\Publish-Pack.ps1 -Version 1.2.2 -InstanceDirectory D:\Pack -Publish
```

发行私钥由作者保存在仓库外，默认路径为 `.tools/signing/updates-private.pem`。请保留可恢复的备份，后续版本使用同一把密钥签名。将生成的清单、签名和文件上传至同一 Release，附件名称保持生成时的名称。

整合包工具需要 Python 3.12 或以上版本。它默认复用上一版公开包的下载索引，按 SHA-1、SHA-512 和文件大小核对下载项。CurseForge 文件变动后，可用 `-Template` 指向更新过的 .mrpack。新配置的归属登记在 `src/ArysLauncher/pack-content-policy.json`。`-NormalizeLocal` 会整理本机 Mod 文件名，将同内容的副本和已卸载 Mod 的配置移入实例的 `.arysmod-backups`。加 `-Publish` 才会上传，工具从 Git Credential Manager 获取当前 GitHub 登录。

整合包更新按发行清单比较文件，并识别同 Mod 的旧 JAR。替换前核对现有文件，准备完整后写入恢复记录，再应用变更。一次性设置同步与 Mod 更新在同一事务中完成，失败时回退，成功后才保存完成标记。以后只更新受管理的 Mod 与资源，玩家设置不再参与文件替换。下载缓存按 1 GiB 上限清理，发行时复用未变化文件的既有下载地址。

启动器更新由副进程等待主程序退出后应用，完成后重新打开。网络失败可重试，应用文件时中断会在下次打开时恢复。

第三方组件的许可证保存在 `docs`，随二进制分发至 `Licenses`。
