# BearDrop

局域网「手机 ↔ 电脑」传文件工具。电脑端开一个本机 HTTP 服务，浏览器里显示二维码；手机扫码用浏览器双向传文件——**手机免装 App、免登录、不经过任何服务器**。只做文件传输，**不执行任何命令**。

## 结构
- `BearDrop.cs` — 单文件 C#（**必须保持 C# 5 兼容**：不用字符串内插、`?.`、`nameof`、`=>` 成员、不用 async/await）。界面文字一律 `L.T("中文","English")`。
- `web/app.html` — 界面（电脑端 + 手机端同一套响应式页面）。
- `web/qrcode.js` — 二维码生成库（qrcode-generator by Kazuhiko Arase，**MIT 许可**）。
- 这两个网页文件在编译时内嵌为资源（名字：`app.html`、`qrcode.js`），运行时从程序集读出。
- `build.bat` — 用 Windows 自带的 .NET Framework 4 `csc` 编译，无需 SDK。

## 工作原理
- 电脑端：启动后用默认浏览器打开 `http://127.0.0.1:<port>/`（**只对本机开放**），页面显示二维码（内含局域网地址 + 随机 token）。
- 手机端：扫码打开 `http://<局域网IP>:<port>/m?k=<token>`，同一套页面。
- 上传（手机→电脑）：一个文件一个 `POST /up?k=token`，文件名放 `X-Filename`（URL 编码），body 就是原始字节——**C# 无需解析 multipart**。保存到「下载\BearDrop」。
- 下载（电脑→手机）：电脑端「添加文件」把磁盘文件加入分享列表（只记路径），手机 `GET /list`、`GET /dl/<id>?k=token` 下载。
- 端口 18650–18659（和 ProjectorCast 同段，首次需防火墙放行「专用网络」）。

## 安全
- 只在局域网、普通 HTTP、**每次启动生成新的随机 token**；`/list`、`/dl`、`/up`、`/m` 都要校验 token。
- 电脑端页面 `/` 只对 loopback（本机）开放，手机必须走带 token 的 `/m`。
- 明显窗口、普通权限、不隐藏运行、不执行命令。

## 本地编译
在 Windows 上双击 `build.bat`（或在仓库根目录推送后由 CI 自动编译，产物进网站 `/downloads/BearDrop.exe`）。

## 发布新版本
改 `BearDrop.cs`，并同步 `src/pages/[lang]/tools/beardrop.astro` 里的 `VERSION`。

## 待办 / 以后
- 代码签名（避免 SmartScreen 警告）。
- 多文件打包下载、断点续传、HTTPS（mkcert）或 Tailscale 支持外网。
- Mac / Linux 版本（界面已是网页，核心服务可用 Go 重写以跨平台）。
