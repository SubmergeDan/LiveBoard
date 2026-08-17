# LiveBoard

[中文说明](README.md)

LiveBoard is a Windows desktop application for live-stream recording and media export, built with .NET Framework 4.8 and WPF.

## Features

- Monitor and record Douyin and Bilibili live rooms.
- Manage multiple rooms with automatic recording, periodic checks, quality selection, and segment settings.
- Sign in to Bilibili with a QR code and remux local M4S audio/video without re-encoding.
- Export public media links from Douyin, Kuaishou, Bilibili, X, and Instagram.
- Bundle FFmpeg, yt-dlp, gallery-dl, and QRCoder. Required tools are extracted to a local cache at runtime.

## Requirements

- Windows 10 or later.
- [.NET Framework 4.8 Runtime](https://dotnet.microsoft.com/download/dotnet-framework/net48).
- To build from source: Visual Studio 2022 / Build Tools with the ".NET desktop build tools" workload and the ".NET Framework 4.8 Targeting Pack".

## One-Click EXE Build

Double-click [`build-release.bat`](build-release.bat) in the repository root, or run:

```bat
build-release.bat
```

The finished executable is written to:

```text
dist\LiveBoard.exe
```

The script uses the system .NET Framework MSBuild and removes the previous `LiveBoard.exe` and PDB from `dist`, leaving only the new executable. Close a running LiveBoard instance before building.

### Manual Build

```bat
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe LiveBoard.csproj /t:Build /p:Configuration=Release /p:Platform=AnyCPU
```

The default output is `bin\Release\LiveBoard.exe`. If MSBuild cannot find the `.NETFramework,Version=v4.8` reference assemblies, install the .NET Framework 4.8 Targeting Pack and build again.

## Usage

1. In **Live Recording**, enter a room ID or live URL, add it to the queue, then start monitoring or recording.
2. In **Bilibili Cache Export**, choose local M4S video and audio files to remux them without re-encoding.
3. In **Media Export**, paste a public sharing URL, wait for analysis, choose an output folder, and export.

Douyin, Bilibili, and other platforms may restrict access because of login state, region, anti-abuse controls, or API changes. Export only content that you are authorized to access and keep.

## Configuration and Privacy

- Configuration is stored in `%LocalAppData%\LiveBoard\config.json`.
- Embedded tools are extracted to `%LocalAppData%\LiveBoard\tools`. Temporary cookie files use the system temporary directory and are removed when the operation finishes.
- LiveBoard does not proactively upload configuration, cookies, or recordings.

## Repository Layout

```text
Assets/       Application icons
Fonts/        Embedded fonts
Libraries/    QRCoder runtime library
Tools/        FFmpeg, yt-dlp, and gallery-dl embedded in the EXE
*.xaml        WPF UI
*.cs          Application source code
```

`bin/`, `obj/`, and `dist/` are build outputs and should not be committed.

## License

This project is released under the [GNU GPL v3.0 or later](LICENSE). GPL-3.0-or-later is used because the distributed package embeds an FFmpeg build configured with GPLv3 components.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party components, their licenses, and upstream source locations.

