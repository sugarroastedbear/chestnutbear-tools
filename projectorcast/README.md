# ProjectorCast

Windows 投屏小工具（单个 C# 源文件，约 2500 行，编译后不到 100KB）。网站下载页：`/en/tools/projectorcast/`、`/zh/tools/projectorcast/`。

## 功能
- **推送视频（DLNA）**：把本地视频或网络视频链接推送到同一 WiFi 下支持 DLNA 的投影仪 / 智能电视。程序内置一个小型媒体服务器（端口范围见 `MediaServer`），首次使用会请求添加防火墙规则。
- **屏幕投屏（HDMI / 扩展屏）**：把某个应用窗口（DWM 缩略图）或整个桌面（GDI 抓屏）全屏输出到投影仪所在的显示器。

## 语言
所有界面文字都写成 `L.T("中文", "English")`。默认跟随 Windows 显示语言（中文系统显示中文，其他英文），用户可在状态栏右下角「语言 / Language」菜单切换，保存在 `%APPDATA%\ProjectorCast\lang.txt`，切换后重启生效。新增文字时也要用 `L.T(...)`；英文更长导致排版不够时用 `L.P(中文数值, 英文数值)`。

## 编译
- 本机：双击 `build.bat`（用 Windows 自带的 .NET Framework 4 编译器，不需要装 SDK）。
- **源码必须保持 C# 5 兼容**：不能用字符串插值 `$"..."`、`?.`、`nameof`、`=>` 成员等新语法，否则 Windows 自带编译器会失败。
- 自动：推送到 main 后，GitHub Actions 在 Windows 上编译，生成的 `ProjectorCast.exe` 和 `.sha256` 放进网站的 `/downloads/` 一起部署（见 `.github/workflows/deploy-site.yml`）。
- Linux 上检查语法：`mcs -langversion:5 -target:winexe -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Xml.dll ProjectorCast.cs`

## 版本
- 1.1.0：中英双语；版本号在源码顶部 `AssemblyVersion`，网站下载页显示的版本在 `src/pages/[lang]/tools/projectorcast.astro` 的 `VERSION`。
