# RyuBar

把媒体条放进 Windows 任务栏。封面、歌词、频谱、进度，一眼看到在放什么。

界面与交互参考 [AF Media Bar](https://github.com/Fervent-Tempo/AF-Media-Bar)，实现为其独立版本。

---

## 功能

| | |
|---|---|
| **媒体条** | 贴在任务栏上的胶囊，内容自适应宽度，空档不够时自动压缩 |
| **封面** | 悬停放大高亮；单击播放/暂停；暂停时常驻播放图标 |
| **歌词** | 双行显示、逐字高亮（有逐字数据时）、长句跑马灯、自动取词 |
| **频谱** | 系统声音环回采集 + FFT 分析，柱数 4~16 可调 |
| **进度** | 环形或条状，可拖动跳转；播放器不支持时显示为禁用态 |
| **二级面板** | 单击歌词弹出：大封面、进度、控制键、输出设备切换、单应用音量 |
| **切歌通知** | 换歌时弹出封面 + 曲名 |
| **托盘菜单** | 切换媒体源、重新连接 SMTC、重新加载条子 |

媒体条上每一项都可以在设置里单独开关。

---

## 安装

解压即用，不需要安装 .NET 运行时。

1. 从 [Releases](../../releases) 下载压缩包
2. 解压，双击 `RyuBar.exe`
3. 媒体条出现在任务栏，托盘出现图标

---

## 使用

```
单击歌词        弹出二级面板（再点一次收起）
双击歌词        打开已经在运行的播放器窗口
单击封面        暂停 / 继续
拖动进度条      跳转（播放器支持时）
滚轮            上一首 / 下一首；Ctrl+滚轮 切换声音来源
右键托盘        切换媒体源 / 重连 SMTC / 重载条子
```

---

## 技术栈

| | |
|---|---|
| 语言 / 框架 | C# · WPF · .NET 10（`net10.0-windows10.0.19041.0`） |
| 任务栏集成 | Win32 —— `SetParent(Shell_TrayWnd)` 把条子做成任务栏子窗口，`SetWindowRgn` 控制命中区域 |
| 媒体信息 | Windows SMTC —— `GlobalSystemMediaTransportControlsSessionManager` |
| 音频采集 | NAudio —— WASAPI 环回 |
| 单应用音量 | `IAudioSessionManager2` / `ISimpleAudioVolume` |
| 输出设备 | `IPolicyConfig::SetDefaultEndpoint`（未文档化接口） |
| 歌词 | Lyricify.Lyrics.Helper + 自有缓存策略 |
| UI 构建 | 除主窗口外全部由代码构建，无 XAML 页面 |

---

## 架构

```
src/
  App.xaml(.cs)                 组合根：单实例、命令行、异常兜底

  Taskbar/                      任务栏宿主
    TaskbarLocator.cs           定位任务栏、采样客户区 / 监视器 / DPI
    TaskbarSampler.cs           一次采样的不可变结果
    MotionPolicy.cs             纯策略：连续采样 → 任务栏是否在动
    RangePolicy.cs              纯策略：占用区 → 空闲区与落点
    OccupancyProbe.cs           UI Automation 探测任务栏已有元素
    TaskbarBarWindow.xaml(.cs)  宿主窗口：挂载、区域、落位、托盘、面板接线

  Components/
    BarView.cs                  条的内容：自绘、宽度自适应、命中判定
    Glyph.cs                    控件图形（几何，不依赖图标字体）

  Media/
    MediaSnapshot.cs            一次媒体快照 + 位置插值
    MediaSessionWatcher.cs      SMTC 通道：会话 / 事件 / 封面 / 控制指令
    SourceNames.cs              AUMID → 友好名
    Readers/                    播放器内存适配（网易云精确进度）

  Lyrics/
    LyricsService.cs            取词与缓存
    LyricsDiskCache.cs          原始文本与解析结果的两级缓存

  Audio/
    SpectrumAnalyzer.cs         WASAPI 环回 + FFT
    AppVolume.cs                单应用音量
    AudioDevices.cs             输出设备枚举与切换

  Menu/ServicePanel.cs          条子上方的二级面板（独立顶层窗口）
  Settings/SettingsWindowTheme.cs  设置窗口（六页）
  Configuration/SettingsStore.cs   设置模型与读写
  Diagnostics/                  日志与 --diag 诊断
  Interop/                      Win32 / 内存 API
```

判断逻辑（`MotionPolicy`、`RangePolicy`）与窗口操作为主的副作用分开：策略是纯函数，输入采样、输出决策，可以单独验证。

---

## 从源码构建

需要 .NET 10 SDK。

```powershell
git clone https://github.com/Strive1012/RyuBar.git
cd RyuBar
.\build.ps1
dotnet publish src\RyuBar.csproj -c Release -o dist
```

单文件自包含（分发给别人用这个）：

```powershell
dotnet publish src\RyuBar.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o dist-single
```

---

## 诊断

排查问题时可在启动前设置环境变量，日志写到 `%LOCALAPPDATA%\RyuBar\logs\ryubar.log`。

| 变量 | 作用 |
|---|---|
| `RYUBAR_LYRIC_TRACE=1` | 歌词滚动的帧率与渲染耗时 |
| `RYUBAR_WIDTH_TRACE=1` | 宽度缓动的死区降到 1px |
| `RYUBAR_FRAME_TRACE=1` | 每帧输出歌词与宽度的关键量 |
| `RYUBAR_NAV_TRACE=1` | 设置窗口的导航与焦点状态 |

---

## 已知限制

1. **条子不能比任务栏高** —— 它是 `Shell_TrayWnd` 的子窗口，绘制被裁在任务栏客户区内
2. **进度和跳转取决于播放器** —— 只依赖 SMTC 上报的数据
3. **逐字高亮需要逐字数据** —— QRC 有，纯 LRC 没有；没有时按整行处理
4. **单应用音量依赖音频会话** —— 极少数播放器不上报会话时会无效
5. **双击歌词对 UWP 应用无效** —— 它们跑在 `ApplicationFrameHost` 里，没有独立窗口可激活

---

## 许可

MIT License，见 [LICENSE](LICENSE)。
