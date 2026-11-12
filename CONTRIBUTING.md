# Contributing to Pynelith

感谢你对 Pynelith 的兴趣。无论是修 Bug、做新组件、改文档还是提设计建议，都欢迎。

## 环境要求

- Windows 10 1809+ 或 Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 可选：Visual Studio 2022 17.8+（“.NET 桌面开发”工作负载）

## 本地构建

```bash
git clone https://github.com/5y2znfz2df-del/Pynelith.git
cd Pynelith

# 编译整个解决方案
dotnet build Pynelith.sln -c Release

# 直接运行灵动岛
dotnet run --project src/Pynelith.Island/Pynelith.Island.csproj -c Release
```

安装器依赖 Island 的发布产物。没有产物时构建会自动发布一次；也可以手动执行：

```bash
dotnet publish src/Pynelith.Island/Pynelith.Island.csproj -c Release -r win-x64 --self-contained false
dotnet publish src/Pynelith.Setup/Pynelith.Setup.csproj -c Release
```

## 模块边界

| 项目 | 职责 | 不要做的事 |
| --- | --- | --- |
| `Pynelith.Glass` | 玻璃渲染、抓屏、折射、回退链 | 不要写业务 UI |
| `Pynelith.Island` | 灵动岛 UI、媒体、歌词、配置、设置窗口 | 不要直接写渲染/Composition 代码，统一走 Glass 门面 |
| `Pynelith.Setup` | 安装、卸载、提权、自启动 | 不要引用 WPF 渲染实现 |

## 提交 PR

1. Fork 仓库，从 `main` 新建分支：`git checkout -b feature/your-idea`
2. 一个 PR 只做一件事，避免混入无关格式化
3. UI 改动请附截图或 GIF（深色 / 浅色、紧凑 / 展开各一张更好）
4. 提交信息说明“为什么改”，而不只是“改了什么”
5. 提交前确认 `dotnet build Pynelith.sln -c Release` 通过；CI 也会在 push / PR 上跑同一命令
6. 不要提交 `bin/`、`obj/`、安装包、日志或任何密钥

## 字体与第三方资源

- 项目当前内置苹方 SC 子集（`src/Pynelith.Island/Fonts/`），由维护者提供分发授权声明。
- 新增其他字体请使用可再分发的开源字体（如 OFL 许可），并在 PR 中附上许可证文本。
- 不要提交来源不明的字体包或安装包。

## 报告问题

提 Issue 时请尽量包含：

- Windows 版本与系统缩放比例
- 播放器名称（网易云 / Spotify / QQ 音乐等）与是否上报 SMTC 时间轴
- 复现步骤、期望结果、实际结果
- 截图或录屏；涉及崩溃时附上事件查看器里的错误信息

## 行为准则

参与本项目即表示你同意遵守 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。
