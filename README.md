# ChestnutBear Tools

Free, open-source Windows tools from [chestnutbear.com](https://chestnutbear.com). Each is a **single C# source file** that compiles with the .NET Framework compiler already included in Windows — no SDK, no dependencies, no installer. The source is here so you can read exactly what it does and build it yourself.

> 来自 [chestnutbear.com](https://chestnutbear.com) 的免费开源 Windows 小工具。每个都是**单个 C# 源文件**，用 Windows 自带的 .NET Framework 编译器就能编译——无需 SDK、无依赖、免安装。源码公开，你可以看清它到底做了什么，也可以自己编译。

## Tools

### 🐻 BearDrop — scan-and-send files between phone and PC
Transfer files between your phone and your PC over the local Wi-Fi by scanning a QR code. Your phone just uses its browser — **no app to install, no login, and files never go through any server**. It only transfers files over your local network and never runs any command.
→ [`beardrop/`](beardrop/)

### 📽️ ProjectorCast — cast your Windows PC to a projector
Send videos to a DLNA projector or smart TV over Wi-Fi, or show any single window (or a whole display) on a projector over HDMI without mirroring your entire desktop.
→ [`projectorcast/`](projectorcast/)

## Download a prebuilt copy

If you'd rather not compile it yourself, download the ready-made `.exe` from the website:

- BearDrop: https://chestnutbear.com/en/tools/beardrop/
- ProjectorCast: https://chestnutbear.com/en/tools/projectorcast/

Because these apps are new and not yet code-signed, Windows SmartScreen and Chrome may warn about an "unknown publisher" / "uncommon file". That is a reputation warning, not a detection of anything harmful — you can verify the download against the SHA-256 checksum shown on each page, or build from source here.

## Build from source (no SDK needed)

You only need Windows — the compiler (`csc.exe`) ships with the .NET Framework that is already installed.

```bat
git clone https://github.com/sugarroastedbear/chestnutbear-tools.git
cd chestnutbear-tools\beardrop
build.bat
```

This produces `BearDrop.exe` in the same folder. Do the same inside `projectorcast\` for `ProjectorCast.exe`. See each tool's own `README.md` for details.

## Privacy & safety

- Both tools work only on your **local network**. Nothing is uploaded to any server.
- They show a clear window the whole time and **never run hidden**.
- BearDrop only moves files; it does **not** execute commands. Each session uses a fresh random code in its QR link, so only a device that scanned it can connect.

## License

[MIT](LICENSE). BearDrop bundles the MIT-licensed [qrcode-generator](https://github.com/kazuhikoarase/qrcode-generator) by Kazuhiko Arase.
