<div align="center">

<img src="assets/icon/png/pynelith-dark-512.png" width="112" alt="Pynelith" />

# Pynelith

**Windows 上的灵动岛 · 真液态玻璃 · 系统媒体接管 · 多源歌词**

在屏幕顶部悬浮一枚会呼吸的胶囊：跟着系统里正在播放的音乐一起形变，显示封面与逐行歌词，
点一下展开详情，全程 60fps。

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white)](#快速开始)
[![License](https://img.shields.io/badge/License-MIT-3DA639)](LICENSE)
[![Status](https://img.shields.io/badge/status-beta%200.1.0-orange)](#roadmap)

<img src="docs/preview.png" width="880" alt="Pynelith 预览" />

</div>

---

## 为什么是 Pynelith

市面上的"Windows 灵动岛"大多只是一个会动的圆角矩形。Pynelith 把三件事同时做对了：

**① 真液态玻璃，不是毛玻璃**
背景**保持清晰**，只在边缘做折射与色散——像一块真的玻璃压住了桌面。渲染能力全部封装在独立模块
`Pynelith.Glass` 里，UI 层零着色器、零 Composition 代码，一行调用即可把玻璃贴在任意元素上。

**② 接管系统媒体，而不是轮询某个播放器**
基于 SMTC（System Media Transport Controls）自动扫描全局会话，优先跟随"正在播放"的那一个。
换歌、播放状态、封面、时间轴全部实时同步；播放/暂停、上一首/下一首通过系统通道下发。

**③ 歌词是真对齐的**
QQ 音乐 / 网易云 / 酷狗 三源自动匹配（标题 + 歌手 + 时长择优），本地缓存，手动导入兜底；
再配一个随时可调的时间偏移——即使歌词源和你的音频版本不同，也能在 10 秒内对齐。

## 特性

<table>
<tr><td width="50%" valign="top">

**外观**
- 真液态玻璃：清晰背景 + 边缘折射 + 色散
- 圆角随尺寸自适应，封面按真实圆角裁剪
- 深色 / 浅色双套图标与配色
- 贴顶居中停靠，支持拖动改位置

**动画**
- 阻尼弹簧形变（固定子步长积分，帧率无关）
- 双层固定布局交叉淡入，动画帧内**零布局重算**
- 玻璃几何刷新节流，把 CPU 留给合成
- 60fps，无跳变、无抽搐

</td><td width="50%" valign="top">

**媒体**
- SMTC 自动扫描 / 跟随 / 播放控制
- 专辑封面、曲名、歌手、播放进度
- 空闲时自动收起为时间胶囊
- 点击胶囊展开详情（大封面 + 曲目 + 歌词）

**歌词**
- 三源自动匹配，本地磁盘缓存
- 手动导入 LRC，立即生效
- 右键 200ms 步进微调整体偏移
- 过滤"作词/作曲/版权"等制作信息行

</td></tr>
</table>

**工程**
- 独立设置窗口，所有参数即时生效（JSON 配置，防抖落盘）
- 位置与 Z 序守护：拦截外部 `SetWindowPos`，防止点击后被系统搬走、被其它窗口遮挡
- 自带原生安装器：管理员提权、系统文件夹对话框选路径、进度/错误反馈、控制面板可卸载

## 快速开始

### 方式一：安装包（推荐）

下载 `beta0.1.0.exe` → 双击 → UAC 允许 → 选择安装位置 → 安装。

安装器会释放程序、创建桌面与开始菜单快捷方式，并注册卸载信息。
**自包含单文件，目标机器无需安装 .NET 运行时。**

### 方式二：从源码构建

```bash
# 环境：Windows 10/11 + .NET 8 SDK
git clone https://github.com/5y2znfz2df-del/Pynelith.git
cd Pynelith

dotnet build src/Pynelith.Island/Pynelith.Island.csproj -c Release
dotnet run  --project src/Pynelith.Island/Pynelith.Island.csproj -c Release
```

构建安装包：

```bash
# 1) 发布主程序（自包含单文件，也就是安装器内嵌的载荷）
dotnet publish src/Pynelith.Island/Pynelith.Island.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true

# 2) 发布安装器（会自动内嵌上一步的 exe）
dotnet publish src/Pynelith.Setup/Pynelith.Setup.csproj -c Release
```

## 使用

| 操作 | 效果 |
| --- | --- |
| 左键单击胶囊 | 展开 / 收起详情（大封面 + 曲目信息） |
| 点击 ⏮ ⏸ ⏭ | 上一首 / 播放暂停 / 下一首（经 SMTC） |
| 拖动胶囊 | 改变停靠位置（自动写回配置） |
| 右键胶囊 | 歌词提前 / 延后 200ms、归零、设置、退出 |

配置位于 `%AppData%\Pynelith\config.json`，歌词缓存位于 `%AppData%\Pynelith\lyrics`。

<details>
<summary><b>主要配置项</b></summary>

| 字段 | 说明 |
| --- | --- |
| `GlobalScale` | 全局缩放（0.5 – 2.0），同时作用于尺寸与排版 |
| `CollapsedWidth` / `CollapsedHeight` | 空闲胶囊尺寸（默认 124 × 24） |
| `ExpandedWidth` | 媒体态宽度（默认 200，高度始终等于空闲高度） |
| `ScreenOffsetX` / `ScreenOffsetY` | 相对"贴顶居中"的偏移 |
| `GlassBlurStrength` | 玻璃染色浓度 0 – 1 |
| `CornerRadius` | 玻璃圆角 |
| `AnimationDurationMs` | 形变动画时长（0 = 关闭动画） |
| `LyricsEnabled` | 是否显示歌词行 |
| `LyricsSourceNetease` / `LyricsSourceQQ` / `LyricsSourceKugou` | 三源开关 |
| `LyricOffsetMs` | 歌词整体偏移（正值 = 提前） |
| `TakeoverNotifications` | 接管系统通知 |

</details>

## 技术栈

| 层 | 选型 |
| --- | --- |
| 运行时 | .NET 8 / WPF（`net8.0-windows10.0.19041.0`） |
| 玻璃渲染 | `Pynelith.Glass`：Win32 + DWM 合成、GDI 抓屏采样、WPF 图层注入 |
| 媒体 | WinRT SMTC（`GlobalSystemMediaTransportControlsSessionManager`） |
| 动画 | `CompositionTarget.Rendering` 阻尼弹簧 + 固定子步长积分 |
| 窗口 | 无边框分层窗口、`WM_WINDOWPOSCHANGING` 拦截、`SetWindowPos` 置顶守护 |
| 配置 | `System.Text.Json`，单实例 + 防抖落盘 |
| 安装器 | WPF 自研：`requireAdministrator`、`OpenFolderDialog`、ICO 内嵌载荷 |

## 项目结构

```
Pynelith/
├─ src/
│  ├─ Pynelith.Glass/      # 液态玻璃渲染模块（对外只有 Install / Update）
│  ├─ Pynelith.Island/     # 灵动岛本体：停靠、形变、媒体、歌词、设置
│  └─ Pynelith.Setup/      # 原生安装器（自包含单文件 + 内嵌载荷）
├─ assets/icon/            # 图标 SVG / ICO / 多尺寸 PNG + 使用规范
├─ docs/preview.png        # README 预览图
├─ demo/                   # 渲染模块示例
└─ Directory.Build.props   # 统一编译配置
```

## 已知限制

- **播放器不报进度时无法自动对齐**：例如网易云的 SMTC 时间轴恒为 0，此时以"换歌事件"作为
  0 点推算，个别曲目仍需用右键偏移手动校准一次。Spotify、QQ 音乐等会上报进度的播放器无需手动干预。
- **玻璃抓屏与动画共用 UI 线程**：玻璃层在 UI 线程做 BitBlt 采样（约 24fps），因此形变动画中
  偶有 1–3 帧被拉长到 35ms 左右。已通过几何刷新节流把影响压到最小。

## Roadmap

- [ ] 逐字（卡拉 OK）歌词高亮
- [ ] 通知接管的完整落地（当前为 SMTC + 通知桥骨架）
- [ ] 多显示器与逐屏 DPI 边界情况
- [ ] 主题跟随系统自动切换深浅图标
- [ ] 玻璃采样移出 UI 线程，彻底消除长帧

## 贡献

欢迎 Issue 与 PR。

1. Fork 本仓库并新建分支：`git checkout -b feature/your-idea`
2. 保持模块边界：**不要**在 Island 或 Setup 里写渲染实现，玻璃能力一律走 `Pynelith.Glass` 的公开门面
3. 提交信息请说明"为什么改"，而不只是"改了什么"
4. 提交 PR 前请确认 `dotnet build` 通过

## License

[MIT](LICENSE) © 2026 FBOJ

<div align="center">
<sub>如果 Pynelith 让你的桌面好看了一点，欢迎点个 ⭐</sub>
</div>
