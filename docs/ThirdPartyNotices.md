# 第三方组件

本启动器的界面、账号存储、导入及下载调度独立实现，正版登录复用 CmlLib 认证组件，未复制 PCL 源码。主视觉来自本 Mod 已有的 `assets/arysdungeonmod/logo.png`。

外置认证设置和内存分配的交互参考龙腾猫跃开发的 [Plain Craft Launcher](https://github.com/Meloong-Git/PCL)，并结合用户提供的界面参考调整。LittleSkin 地址依据其[官方客户端说明](https://manual.littleskin.cn/yggdrasil/client)。这些项目不作为本启动器的二进制依赖。

| 组件 | 版本 | 许可 | 来源 |
| --- | --- | --- | --- |
| CmlLib.Core | 4.1.0 | MIT | https://github.com/CmlLib/CmlLib.Core |
| CmlLib.Core.Commons | 4.0.0 | MIT | https://github.com/CmlLib/CmlLib.Core.Commons |
| CmlLib.Core.Auth.Microsoft | 3.4.0 | MIT | https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft |
| XboxAuthNet.Game | 1.5.0 | MIT | https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft |
| XboxAuthNet | 3.1.0 | MIT | https://github.com/AlphaBs/XboxAuthNet |
| Microsoft.Extensions.Logging.Abstractions / DependencyInjection.Abstractions | 8.0.1 | MIT | https://github.com/dotnet/runtime |
| CmlLib.Core.Installer.Forge | 2.0.0 | MIT | https://github.com/CmlLib/CmlLib.Core.Installer.Forge |
| HtmlAgilityPack | 1.11.48 | MIT | https://github.com/zzzprojects/html-agility-pack |
| LZMA-SDK | 19.0.0 | MIT | https://github.com/monemihir/LZMA-SDK |
| SharpZipLib | 1.4.2 | MIT | https://github.com/icsharpcode/SharpZipLib |
| Microsoft.Web.WebView2 SDK | 1.0.4314.35 | Microsoft 许可，见随附文件 | https://learn.microsoft.com/microsoft-edge/webview2/ |
| Microsoft WebView2 Fixed Version Runtime x64 | 156.0.4314.58 | Microsoft WebView2 分发许可及组件原附通知 | https://developer.microsoft.com/en-us/microsoft-edge/webview2/ |
| Windows SDK .NET 投影及 WinRT 运行依赖 | 10.0.17763.57 目标包 | Microsoft Windows SDK 许可 | https://aka.ms/WinSDKLicenseURL |
| .NET Windows Desktop Runtime | 10.0.14 | MIT 及随附第三方许可 | https://github.com/dotnet/runtime |

`Build.ps1 -Publish` 会将本目录的许可文件与通知复制到构建产物的 `Licenses` 目录。.NET 分发包含的第三方通知也应随产物保留。

随附浏览器组件从微软官方 Fixed Version Runtime CAB 原样展开，构建时核对 SHA-256 `857DCFD6F7035508A29645FA2978C3A6A841F220DF77CC366D318894E19F4792` 及主程序的 Microsoft 签名。`WebView2Runtime` 中原附的许可、第三方通知与显示许可的入口随组件一起保留。优先使用系统 Evergreen，固定版作为无须另行安装的后备运行环境。

authlib-injector 是在玩家选择外置认证后单独获取的 Java 代理组件，本初版没有将其 JAR 内置到仓库。其许可与源码见 https://github.com/yushijinhun/authlib-injector 。Minecraft、Forge 以及整合包内各 Mod 和资源的版权归各自权利人所有，整合包打包应遵守各文件的分发条款。
