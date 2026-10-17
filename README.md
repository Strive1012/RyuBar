# RyuBar

把媒体条放进 Windows 任务栏。封面、歌词、频谱、进度，一眼看到在放什么。

界面与交互参考 [AF Media Bar](https://github.com/Fervent-Tempo/AF-Media-Bar)，实现为其独立版本。

---

## 功能

| | |
|---|---|
| **媒体条** | 贴在任务栏上的胶囊，内容自适应宽度，任务栏空档不够时自动压缩 |
| **封面** | 悬停放大 + 高亮 + 中心按钮；单击播放/暂停；暂停时常驻播放图标 |
| **歌词** | 双行显示、逐字高亮（有逐字数据时）、长句跑马灯、自动取词（QQ音乐 / 网易云 / 跨来源兜底） |
| **频谱** | WASAPI 环回采集 + 自写 FFT，柱数 4~16 可调 |
| **进度** | 环形或条状，可拖动跳转；播放器不支持跳转时显示为禁用态 |
| **二级面板** | 单击歌词弹出：大封面、进度、控制键、**输出设备切换**、**单应用音量** |
| **切歌通知** | 换歌时弹出封面 + 曲名 |
| **托盘菜单** | 切换媒体源、重新连接 SMTC、重新加载条子 |

**媒体条上的每一项都可以在设置里单独开关**，全关掉也能用。

---

## 安装

**解压即用，不需要装 .NET 运行时。**

1. 从 [Releases](../../releases) 下载 `RyuBar-1.0.0.zip`
2. 解压，双击 `RyuBar.exe`
3. 媒体条出现在任务栏，托盘出现图标

大于 50 MB 是因为**自包含**（.NET 运行时打在里面）。如果你已经装了 .NET 10 Desktop Runtime，可以自己 `dotnet publish` 出约 5 MB 的框架依赖版。

---

## 使用

完整说明见 [使用手册](docs/使用手册.md)。几个最常用的：

```
单击歌词        弹出二级面板（再点一次收起）
双击歌词        打开已经在运行的播放器窗口
单击封面        暂停 / 继续
拖动进度条      跳转（播放器支持时）
滚轮            上一首 / 下一首；Ctrl+滚轮 切换声音来源
右键托盘        切换媒体源 / 重连 SMTC / 重载条子
```

---

## 技术要点

**WPF / .NET 10**，单进程单 exe。

### 条子为什么能贴在任务栏上

```
WS_POPUP → WS_CHILD + SetParent(Shell_TrayWnd)
SetWindowRgn 只把"媒体条那一块"设成可命中，其余像素让给任务栏
```

代价：条子是子窗口，尺寸被钳在任务栏客户区内 —— **这就是"条子不能比任务栏高"的根本原因**。

### 媒体信息来源

Windows 的 **SMTC**（`GlobalSystemMediaTransportControlsSessionManager`）。
不注入播放器、不读界面、不猜。所以：

- 播放器不上报时间轴 ⇒ 就没有进度可显示
- 播放器不支持 seek ⇒ 进度条就置灰
- 这都不是 bug，是数据源的限制

### 频谱

`NAudio` 负责 WASAPI 环回采集，频谱分析由本项目实现：
Hann 窗 + 512 点 radix-2 FFT + 对数分布 16 频段 + 快起慢落。

### 单应用音量 / 输出设备

- 音量：`IAudioSessionManager2` 按进程 id 找到音频会话，读写 `ISimpleAudioVolume`
- 输出设备：未文档化的 `IPolicyConfig::SetDefaultEndpoint`（AFM、EarTrumpet 走的都是它）

### 60fps 动画

核心三条：

1. **每帧 `SetWindowPos` 带 `SWP_FRAMECHANGED`** 会重建分层窗口表面 ⇒ 帧间隔硬上限 ~22ms。动画必须搬到"内容布局"，窗口宽度一次到位。
2. **`DispatcherTimer(16ms)` 会被 UI 线程上别的定时器挤后** ⇒ 换成 `CompositionTarget.Rendering`，并限流到 16ms 且跳过时不累加。
3. **动画的起止必须用同一量纲**（内容 DIP），混用窗口像素会导致"终点与终态不一致 ⇒ 结束跳一下"。

---

## 从源码构建

需要 **.NET 10 SDK**。

```powershell
git clone https://github.com/Strive1012/RyuBar.git
cd RyuBar

# 普通构建
.\build.ps1
dotnet publish src\RyuBar.csproj -c Release -o dist

# 单文件自包含（分发给别人用这个）
dotnet publish src\RyuBar.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o dist-single
```

---

## 架构

```
src/
  App.xaml(.cs)                 组合根：单实例、命令行、异常兜底、命名事件退出
  app.manifest                  PerMonitorV2 + asInvoker

  Interop/
    NativeMethods.cs            Win32 接口（最小集）

  Taskbar/
    TaskbarLocator.cs           定位任务栏、客户区/监视器/DPI 采样、隐藏判定
    TaskbarSampler.cs           一次采样的不可变结果
    MotionPolicy.cs             ★纯策略：连续采样 → 在动/稳定
    RangePolicy.cs              ★纯策略：占用区 → 空闲区与落点
    OccupancyProbe.cs           UIA 探测（MTA 线程 + 最新请求队列）
    TaskbarBarWindow.xaml(.cs)  宿主：挂载、区域、落位、冻结、恢复、托盘、面板接线

  Components/
    BarView.cs                  条的内容：自绘 + 内容自适应宽度 + 命中判定
    Glyph.cs                    控件图形（几何，不依赖字体图标）

  Media/
    MediaSnapshot.cs            一次媒体快照 + 位置插值（没有时间轴就不外推）
    MediaSessionWatcher.cs      SMTC 通道：会话 / 事件 / 封面 / 控制指令
    SourceNames.cs              AUMID → 友好名 / 进程名
    Readers/                    播放器内存适配（网易云精确进度）

  Lyrics/
    LyricsService.cs            取词与缓存（QQ 音乐 / 网易云 / 跨来源兜底）
    LyricsDiskCache.cs          原始文本与解析结果的两级缓存

  Audio/
    SpectrumAnalyzer.cs         WASAPI 环回 + FFT
    AppVolume.cs                单应用音量（音频会话）
    AudioDevices.cs             输出设备枚举与切换（IPolicyConfig）

  Menu/
    ServicePanel.cs             条子上方的二级面板（独立顶层窗口）

  Settings/
    SettingsWindowTheme.cs      设置窗口（六页，代码构建 UI）

  Configuration/
    SettingsStore.cs            设置模型与读写

  Diagnostics/
    Log.cs                      单文件日志（按大小轮转）
    Diag.cs                     --diag：把整条链路独立跑一遍
```

---

## 诊断

排查问题时可以在启动前设环境变量：

| 变量 | 作用 |
|---|---|
| `RYUBAR_LYRIC_TRACE=1` | 歌词滚动的帧率与每帧渲染耗时 |
| `RYUBAR_WIDTH_TRACE=1` | 宽度缓动死区降到 1px |
| `RYUBAR_FRAME_TRACE=1` | 每帧输出歌词/宽度关键量（定位抖动、闪） |
| `RYUBAR_NAV_TRACE=1` | 设置窗口的导航与焦点状态 |

日志：`%LOCALAPPDATA%\RyuBar\logs\ryubar.log`

---

## 已知限制

1. **条子不能比任务栏高** —— 它是 `Shell_TrayWnd` 的子窗口，绘制被裁在任务栏客户区内
2. **进度/跳转取决于播放器** —— 只上报 SMTC 事件的播放器（网易云、QQ音乐部分版本）没有时间轴
3. **逐字高亮需要逐字数据** —— QRC 有，纯 LRC 没有。没有时按整行处理，不猜测每个字的时间
4. **单应用音量依赖音频会话** —— 极少数播放器不上报会话时会无效
5. **双击歌词对 UWP 应用无效** —— 它们跑在 `ApplicationFrameHost` 里，没有独立窗口可激活

---

## 许可

MIT License，见 [LICENSE](LICENSE)。
