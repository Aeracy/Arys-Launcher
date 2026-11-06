# Ary's Launcher

作者 Aeracy。

这是《少女の永恒地牢》整合包使用的 Windows x64 启动器。支持 Microsoft 正版登录、LittleSkin 和自定义 Yggdrasil 认证，可导入 Forge 整合包，管理 Java、内存、下载和更新。

启动器源码在本仓库维护。ArysMod 的构建产物随 Release 发布，Mod 源码由作者在独立仓库维护。

## 使用

下载 Release 中的安装程序，或解压便携包。首次使用时导入整合包，在账号页登录后启动游戏。

游戏文件保存在启动器自己的 `.minecraft` 中，账号与设置保存在 `data`。Microsoft 授权在内嵌的官方网页中完成。

## 构建

安装 .NET 10 SDK，在 Windows PowerShell 中运行。

```powershell
.\Build.ps1
.\Build.ps1 -Package
```

打包需要 NSIS 3.13，可放入 `.tools/nsis-3.13` 或通过 `NSIS_HOME` 指定目录。构建脚本获取并校验微软的离线 WebView2 组件，使用单节点编译，输出位于 `app` 和 `artifacts`。

主程序是包含 .NET 运行时的压缩 exe。安装程序优先复用系统 WebView2，缺少时保留离线组件，在首次登录时解压。

轻量便携包使用系统 WebView2。需要离线备用组件时，使用完整安装程序。

## 发布更新

`release-channel.json` 保存公开仓库、整合包清单地址和发行公钥。启动器与整合包分别发布版本。文件路径、大小及 SHA-256 写入更新清单，清单使用 ECDSA P-256 签名。

```powershell
.\New-UpdateRelease.ps1 -Kind Launcher -Version 1.0.1
.\New-UpdateRelease.ps1 -Kind Pack -Version 1.0.1 -ContentRoot D:\Pack -Include 'mods/arysdungeonmod-*.jar' -PreviousManifest .\previous-pack.json
```

发行私钥由作者保存在仓库外，默认路径为 `.tools/signing/updates-private.pem`。请保留可恢复的备份，后续版本使用同一把密钥签名。将生成的清单、签名和文件上传至同一 Release，附件名称保持生成时的名称。

整合包更新只处理发行清单登记的文件。替换前核对现有文件，准备完整后写入恢复记录，再应用变更。新增文件与旧文件的移除可跨版本执行。本地文件与已知发行版本不一致时，界面会指出冲突位置。下载缓存按 1 GiB 上限清理。

启动器更新由副进程等待主程序退出后应用，完成后重新打开。网络失败可重试，应用文件时中断会在下次打开时恢复。

第三方组件的许可证保存在 `docs`，随二进制分发至 `Licenses`。
