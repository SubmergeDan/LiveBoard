# LiveBoard

[English README](README.en.md)

LiveBoard 是一个 Windows 桌面直播录制与媒体导出工具，基于 .NET Framework 4.8 和 WPF 构建。

## 功能

- 监控并录制抖音、Bilibili 直播间。
- 支持多直播间队列、自动录制、定时检查、画质与分片设置。
- 支持 Bilibili 二维码登录和 M4S 音视频无损封装。
- 支持导出抖音、快手、Bilibili、X 和 Instagram 的公开媒体分享链接。
- 内置 FFmpeg、yt-dlp、gallery-dl 和 QRCoder，运行时会将所需工具释放到本机缓存目录。

## 系统要求

- Windows 10 或更高版本。
- [.NET Framework 4.8 Runtime](https://dotnet.microsoft.com/download/dotnet-framework/net48)。
- 仅在从源码构建时：Visual Studio 2022 / Build Tools 的“.NET 桌面生成工具”和“.NET Framework 4.8 Targeting Pack”。

## 一键构建 EXE

双击根目录的 [`build-release.bat`](build-release.bat)，或在命令行中执行：

```bat
build-release.bat
```

构建成功后，EXE 位于：

```text
dist\LiveBoard.exe
```

脚本使用系统自带的 .NET Framework MSBuild，并会清理 `dist` 中旧的 `LiveBoard.exe` 与 PDB，使发布目录只保留新的 EXE。构建前请关闭正在运行的 LiveBoard。

### 手动构建

```bat
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe LiveBoard.csproj /t:Build /p:Configuration=Release /p:Platform=AnyCPU
```

默认输出路径为 `bin\Release\LiveBoard.exe`。如果提示找不到 `.NETFramework,Version=v4.8` 引用程序集，请安装 .NET Framework 4.8 Targeting Pack 后重试。

## 使用说明

1. 在“直播录制”中输入房间号或直播链接，添加到队列后可开始监控和录制。
2. 在“B站缓存导出”中选择本地 M4S 视频、音频文件，进行无损封装。
3. 在“媒体导出”中粘贴公开分享链接，解析完成后选择保存位置并导出。

抖音、Bilibili 等平台可能因登录、地区、风控或平台接口变更而限制访问。请仅导出你有权访问和保存的内容。

## 配置与隐私

- 配置保存在 `%LocalAppData%\LiveBoard\config.json`。
- 内置工具释放到 `%LocalAppData%\LiveBoard\tools`；临时 Cookie 文件使用系统临时目录，并在操作结束后删除。
- LiveBoard 不会主动上传配置、Cookie 或录制内容。

## 仓库结构

```text
Assets/       应用图标
Fonts/        内置字体
Libraries/    QRCoder 运行库
Tools/        打包进 EXE 的 FFmpeg、yt-dlp、gallery-dl
*.xaml        WPF 界面
*.cs          应用源码
```

`bin/`、`obj/` 和 `dist/` 均为构建输出，不应提交到 Git。

## 许可证

本项目以 [GNU GPL v3.0 或更高版本](LICENSE) 发布。选择 GPL-3.0-or-later 是因为发行包内置了启用 GPLv3 的 FFmpeg 构建。

第三方组件及其许可证、源码获取地址见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

