# RyuBar

Windows 10/11 任务栏媒体条与歌词 —— **自研实现**。

参照物是 AF Media Bar 的**外观风格与交互**（它的设计令牌、行为规格是我做外观对齐时的依据），
但**代码全部是自己写的**：任务栏宿主、媒体通道、渲染、歌词引擎都不复制任何第三方项目源码。

---

## 现状

| 阶段 | 内容 | 状态 |
|---|---|---|
| **1** | **任务栏宿主骨架**：子窗口化、区域穿透、UI Automation 占用探测、运动冻结、自动隐藏跟随 | ✅ 已完成，真机 6/6 通过 |
| **2** | **媒体与交互**：SMTC 会话/快照/位置插值、播放/暂停/上下首/跳转、点击/滚轮/悬停、内容自适应宽度 | ✅ 完成 |
| 3 | 外观对齐：设计令牌 → 胶囊/描边/顶部高光/模糊封面材质/悬停色板/封面进度环/长标题滚动 | 待开始 |
| **5a** | **内存适配器（提前做）**：网易云的真实进度（内存自校准扫描）+ 真实时长（客户端本地 JSON）+ 跳转真实性验证 | ✅ 完成 |
| 5b | 其他内存适配器（酷狗用指针链）、歌词、频谱 | 待开始 |
| 4 | 歌词：WebView2 宿主 + 逐字高亮前端 + 取词（Lyricify 公开库） | 待开始 |
| 5 | 其余功能：曲目切换通知 / 频谱 / 读内存适配 / 更新器 | 待开始 |
| 6 | 设置界面 + 打包 | 待开始 |

---

## 阶段 1 做了什么

1. **变成任务栏的子窗口**：`WS_POPUP → WS_CHILD` + `SetParent(Shell_TrayWnd)`，
   宿主铺满整条任务栏**客户区**（坐标一律用父窗口客户区原点，避免任务栏移动时的坐标漂移）。
2. **区域穿透**：`SetWindowRgn` 只把"媒体条那一块"设成可命中，其余像素完全让给任务栏 ——
   任务栏图标照常可点、开始菜单照常可开。
3. **占用区探测**：UI Automation（跑在专用 MTA 线程，队列只留最新请求）把任务栏上已有的元素量出来，
   过滤掉本进程窗口，交给纯策略 `RangePolicy` 挑出左侧空闲区。
4. **运动冻结**：任务栏滑入滑出时什么都不做，**连续两次采样一致**才一次性落定几何与区域。
5. **自动隐藏跟随 + 淡入**：任务栏隐藏时**不隐藏宿主**（它跟着父窗口一起离屏，只把透明度归零），
   任务栏露出的那一帧开始 **150 ms 线性淡入** —— 时长是按实测对齐的（见下面"测量工具"）。
   隐藏期间采样周期提到 10 ms，才能在同帧起淡入。
6. **Explorer 重启恢复**：监听 `TaskbarCreated`；更关键的是——杀 Explorer 会**连带销毁**我们的子窗口
   （条挂在任务栏下面），所以宿主会通知持有方**重建窗口**（`HostInvalidated` → App 在下一帧重建），
   实测从旧宿主死亡到重新挂上约 **0.3 秒**。

## 架构

```
src/
  App.xaml(.cs)                 唯一组合根：单实例、命令行、异常兜底、命名事件退出
  app.manifest                  PerMonitorV2 + asInvoker（任务栏功能不需要管理员）
  Interop/
    NativeMethods.cs            用到的全部 Win32 接口（最小集）
  Taskbar/
    TaskbarLocator.cs           定位任务栏、客户区/监视器/DPI 采样、隐藏判定
    TaskbarSampler.cs           一次采样的不可变结果
    MotionPolicy.cs             ★纯策略：连续采样 → 在动/稳定
    RangePolicy.cs              ★纯策略：占用区 → 空闲区与落点
    OccupancyProbe.cs           UIA 探测（MTA 线程 + 最新请求队列）
    TaskbarBarWindow.xaml(.cs)  宿主窗口：挂载、区域、落位、冻结、恢复
  Components/
    BarView.cs                  条的内容：自绘 + 内容自适应宽度 + 命中区域
    Glyph.cs                    传输控件图形（几何，不依赖字体图标）
  Media/
    MediaSnapshot.cs            一次媒体快照 + 位置插值（没有时间轴就不外推）
    MediaSessionWatcher.cs      SMTC 通道：会话/事件/封面/控制指令 + 跳转真实性验证
    Readers/
      ModuleMemoryScanner.cs    进程模块内存扫描底座（OpenProcess/VirtualQueryEx/ReadProcessMemory）
      NetEaseMemoryReader.cs    网易云播放位置：自校准找出"按时间推进的那个 double"
      NetEaseMetadataIndex.cs   网易云时长：读客户端本地 playingList/fmPlay JSON
      NetEaseProbe.cs           侦察工具（--diag netease）
  Diagnostics/
    Log.cs                      单文件日志（按大小轮转）
    Diag.cs                     --diag：把整条链路独立跑一遍
tools/
  verify-phase1.ps1             阶段 1 验收脚本（可直接重跑，加 -Explorer 测重启恢复）
  inspect-host.ps1              窗口区域 / 命中归属 / 点击穿透 的人工排查
  inspect-zorder.ps1            任务栏子窗口的 Z 序（确认我们没被内容桥盖住）
  bar-click.ps1                 按应用上报的控件矩形**真实点击**条上的控件（play/prev/next/seek）
  measure-reveal-patches.ps1    ★ 淡入/淡出测量：BitBlt 小块区域 + 亮度均值，约 3 ms 一格
  measure-reveal.ps1            任务栏窗口矩形的高频采样（证明矩形是跳变而非滑动）
  measure-reveal-pixels.ps1     单像素列扫描（保留作参考，精度不如 patches 版）
  burst-reveal.ps1              露出过程连拍拼图（人工目视用）
```

★ = 纯策略，不碰任何系统接口，可以单独写单元测试。

## 构建与运行

```powershell
powershell -File .\build.ps1                 # Release 构建
powershell -File .\build.ps1 -Run            # 构建并启动
powershell -File .\build.ps1 -SelfContained  # 单文件自包含发布到 dist\
```

```powershell
RyuBar.exe            # 启动（单实例）
RyuBar.exe --diag     # 诊断：任务栏几何 / 占用区 / 选点 / 运行中实例状态
RyuBar.exe --exit     # 让运行中的实例退出
RyuBar.exe --help
```

- 日志：`%LOCALAPPDATA%\RyuBar\logs\ryubar.log`
- 状态：`%LOCALAPPDATA%\RyuBar\state.json`（后端、挂载、条的真实位置尺寸、占用区、探测来源）
- **设置：`%LOCALAPPDATA%\RyuBar\settings.json`** —— 改完**自动生效**（约 1 秒内读入），不需要重启

### 设置文件（当前只有淡入/淡出）

```json
{
  "schemaVersion": 1,
  "fade": {
    "fadeInEnabled": true,          // 露出时是否淡入
    "fadeInMilliseconds": 150,      // 淡入时长
    "fadeInDelayMilliseconds": 150, // 淡入延后：等任务栏自己的露出动画画完再开始
    "fadeOutEnabled": true,         // 隐藏时是否淡出
    "fadeOutMilliseconds": 150      // 淡出时长
  }
}
```

取值都会被收敛到 0–2000 ms；`0` 表示该动作瞬时完成。文件不存在时首次运行会写一份默认值。

> **淡出为什么需要"位移抵消"**：任务栏自动隐藏时，它的窗口矩形是**一步跳到屏幕外**的，条作为子窗口会被一起带走（瞬间消失）。
> 所以开启淡出后，我们会在隐藏那一瞬间**反向抵消这个位移**，把条按在原位淡出，淡出结束再放回正常位置。
> 日志里能看到 `淡出开始 150 ms（线性；抵消父窗口位移 70 px）`。

## 阶段 1 验收结果（本机实测）

```
[PASS] 1. attached and inside the taskbar client area
        attached=True client=2560x72 bar=(12,9) 630x54 reason=fit
[PASS] 2. bar does not overlap any taskbar element
        occupied ranges=13; overlaps=none
[PASS] 3. hit test - inside is ours, outside is the taskbar
        inside pid=5884 (ours), outside pid=13720 (explorer)
[PASS] 4. clicking a taskbar icon (outside the bar) reaches the taskbar
        foreground pid 0 -> 28260
[PASS] 5. clicking inside the bar does not steal focus or minimize
        foreground minimized=False
[PASS] 6. follows the taskbar auto-hide
        when taskbar hidden: hidden=True; after reveal: hidden=False
[PASS] 7. recovers after an Explorer restart            (需要 -Explorer 开关，任务栏会闪一下)
        taskbar handle 0x304DA -> 0x303AC; state rewritten after the kill=True; bar hit-testable again=True
summary: 7/7 passed, 0 failed
```

重跑：`powershell -File .\tools\verify-phase1.ps1`（加 `-Explorer` 会额外做一次 Explorer 重启恢复测试，那一步会让任务栏闪一下）。

## 已知行为与限制

- **滚轮**：光标在条上滚动 = 上一首 / 下一首（上 = 上一首，下 = 下一首），别的窗口不会被误滚。
  这条依赖系统设置 `HKCU\Control Panel\Desktop\MouseWheelRouting = 2`（设置 → 鼠标 → **悬停即滚动**）。
  若该设置为 1（只在焦点窗口滚动），滚轮会交给焦点应用而不给条 —— 那种情况下才需要低级鼠标钩子。
- **点条不会激活任何窗口**：宿主是 `WS_EX_NOACTIVATE` 且吞掉 `WM_MOUSEACTIVATE`。
  副作用是当前前台窗口会失去焦点（和点任务栏空白处一致），但不会被最小化。
- **露出时淡入 150 ms（线性）**，并且**延后 150 ms 开始**：任务栏自己的露出动画也在同一时刻跑，
  我们的逐帧透明度更新会被 DWM 抢占，表现为"淡入中途卡一下、然后跳完"；延后之后曲线恢复平滑。
  实测中任务栏的"帧"先到、内容约 85 ms 后才开始淡入，所以这个延后让条比图标略晚、但两者不再互相挤。
  两个参数都在 `TaskbarBarWindow` 顶部：`FadeDelayMilliseconds`、`FadeInMilliseconds`。

### 诊断开关（环境变量，默认关闭）

| 变量 | 作用 |
|---|---|
| `RYUBAR_FADE_TRACE=1` | 把淡入的每一帧时间与透明度写进日志（判断"卡顿是渲染被饿住还是动画没走"） |
| `RYUBAR_FADE_TEST=900` | 任务栏静止时每 900 ms 重放一次淡入（隔离测量"卡顿是否来自任务栏自身动画"） |
- **只处理主任务栏**：多显示器的 `Shell_SecondaryTrayWnd`、竖向任务栏留到后续阶段。
- **占用区探测只认 Explorer 进程的任务栏元素**：第三方覆盖窗口（如 TranslucentTB 的额外层）暂未识别。
- **空间不足时整条隐藏**（并写日志说明原因），不做压窄到看不见的条。
- 条的外观目前是阶段 1 的占位胶囊，真正的观感在阶段 3 做。
