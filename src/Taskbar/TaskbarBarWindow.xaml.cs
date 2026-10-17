using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RyuBar.Diagnostics;
using RyuBar.Interop;

namespace RyuBar.Taskbar;

/// <summary>
/// 任务栏媒体条的宿主窗口。阶段 1 只负责"存在得正确"：
///
///  1. 变成任务栏的子窗口（WS_CHILD + SetParent），窗口**就是条本身**（客户区坐标定位），
///     这样任务栏滑入滑出、被裁剪时，条跟着一起动；圆角外的透明像素天然是点击穿透。
///  2. 用 UI Automation 量出已占用区间，把条放进左侧空闲区，绝不压在原生小组件上。
///  3. 运动冻结：任务栏在动的时候什么都不做，连续两次采样一致才一次性落定。
///  4. 任务栏自动隐藏时**不隐藏宿主**：它跟着父窗口一起离屏，只把透明度归零；
///     任务栏露出的那一帧开始淡入，淡入时长按实测对齐（约 150 ms 线性）。
///  5. Explorer 重启后重新挂载。
/// </summary>
public partial class TaskbarBarWindow : Window
{
    /// <summary>
    /// 任务栏稳定时的采样周期。
    /// 稳定状态任务栏不会动，没必要 20 次/秒地唤醒 UI 线程；
    /// 真正的运动检测靠"任务栏隐藏时"的快速节拍（<see cref="HiddenTickMilliseconds"/>）。
    /// </summary>
    private const int TickMilliseconds = 150;

    /// <summary>任务栏自动隐藏时的采样周期：要快到能在露出的那一帧就开始淡入。</summary>
    private const int HiddenTickMilliseconds = 10;

    /// <summary>轮询设置文件是否被改动的间隔（改完立即生效，不需要重启）。</summary>
    private const long SettingsPollMilliseconds = 1000;

    /// <summary>
    /// 宿主窗口已经不可用（HWND 被销毁）。Explorer 重启会销毁任务栏窗口，而我们挂在它下面，
    /// 于是子窗口也被一起销毁 —— 这种情况不能靠重新 SetParent 救回来，必须由持有方重建窗口。
    /// </summary>
    public event Action? HostInvalidated;

    private bool _invalidated;
    private readonly Configuration.SettingsStore _settings;
    private readonly Media.MediaSessionWatcher _media;

    private const int EdgePaddingDip = 8;
    // 上下留白改成 0：条子占满任务栏客户区高度（用户要求"条子变大一圈" ✓ 约 36 → 48 DIP ✓）
    private const double TopMarginDip = 0;
    private const int GapDip = 12;
    private const int DesiredWidthDip = 420;
    private const int MinimumWidthDip = 150;
    private const long ProbeSafetyMilliseconds = 15000;

    private const long FingerprintIntervalMilliseconds = 500;

    private long _lastFingerprintAt;

    private int _lastWindowSignature;

    /// <summary>露出后抑制探测的时长：让任务栏自己的露出动画先跑完。</summary>
    private const long ProbeSuppressAfterRevealMilliseconds = 600;

    /// <summary>可见期间定期把自己抬到同级窗口最上面的间隔。</summary>
    private const long RaiseIntervalMilliseconds = 1000;

    private readonly MotionPolicy _motion = new();
    private readonly OccupancyProbe _probe = new();
    private readonly DispatcherTimer _timer;

    private IntPtr _hwnd;
    private IntPtr _taskbar;
    private uint _taskbarCreatedMessage;

    private bool _attached;
    private bool _frozen = true;

    /// <summary>只有"没地方放"或 Explorer 恢复时才真正 SW_HIDE。</summary>
    private bool _selfHidden;

    /// <summary>任务栏自己在屏幕外：宿主保留（跟着父窗口离屏），只把透明度归零。</summary>
    private bool _taskbarHidden;

    private bool _needsPlace = true;

    /// <summary>最近一次由摆位决定的"强制整条宽度"（DIP，0=自适应）。两处施加点共用它，避免互相覆盖导致抽搐。</summary>
    private int _appliedForcedWidthDip;

    /// <summary>「歌词自动适配」模式的文字区上限（DIP）= 任务栏能给文字区的最大宽度（实测）。写进 state.json 供设置界面防呆。</summary>
    private int _maxTextWidthDip;

    /// <summary>**当前配置**实际能放下的文字区宽度（DIP）—— 与 _maxTextWidthDip 的差别就是可选部件的宽度。</summary>
    private int _liveTextWidthDip;

    /// <summary>任务栏能给媒体条的可用宽度（DIP，落位策略实测）—— 诊断用，写进 state.json。</summary>
    private int _usableDip;

    /// <summary>当前配置的"文字区之外"占宽（诊断用）。</summary>
    private int _chromeDip;

    /// <summary>必需部件的占宽（诊断用）。</summary>
    private int _minChromeDip;

    // ---- 频谱：WASAPI 环回采集 + 自写 FFT（零第三方 ✓ 见 Audio/SpectrumAnalyzer.cs）----
    private Audio.SpectrumAnalyzer? _spectrum;
    private readonly float[] _spectrumBuffer = new float[Audio.SpectrumAnalyzer.BandCount];

    /// <summary>
    /// 频谱采集器：**按需启动** ✓（设置里开了频谱才启动 ✓ 关掉就 Dispose ✓ 不占 CPU ✓）。
    /// 采集在后台线程 ✓ UI 线程只做一次数组拷贝 ✓（16 个 float ✓ 无锁 ✓ 不阻塞渲染 ✓）。
    /// </summary>
    private void EnsureSpectrumAnalyzer()
    {
        bool want = _settings.Current.Bar.Spectrum;
        if (want && _spectrum is null)
        {
            _spectrum = new Audio.SpectrumAnalyzer();
            _spectrum.Start();
            Log.Write("spectrum", $"启动采集：running={_spectrum.IsRunning} 错误=\"{_spectrum.LastError}\"");
        }
        else if (!want && _spectrum is not null)
        {
            _spectrum.Dispose();
            _spectrum = null;
            Log.Write("spectrum", "已停止采集（设置里关掉了 ✓）");
        }
    }

    /// <summary>把最新频段数据推给视图（在 60fps 那一拍里调用 ✓ 顺手完成 ✓ 不额外开线程 ✓）。</summary>
    private void PushSpectrumLevels()
    {
        if (_spectrum is null)
        {
            Bar.SetSpectrumLevels(_spectrumBuffer, hasRealData: false);
            return;
        }

        _spectrum.CopyBands(_spectrumBuffer);
        Bar.SetSpectrumLevels(_spectrumBuffer, hasRealData: _spectrum.IsRunning);
    }


    // ---- 宽度缓动（歌词换句时条子平滑变宽/变窄，而不是瞬间跳）----
    private System.Windows.Threading.DispatcherTimer? _widthTimer;
    private int _appliedBarWidthPx = -1;   // 当前实际宽度（物理像素，-1 = 还没摆过位）
    private int _widthAnimFrom;
    private int _widthAnimTo;
    private DateTime _widthAnimStart;
    private int _widthAnimTop;
    private int _widthAnimBottom;
    private int _widthAnimDurationMs = 220;
    private int _widthAnimFrames;
    private bool _widthAnimRolling;

    /// <summary>把条宽从当前值缓动到目标值（默认 220ms、CubicEaseOut、16ms 一帧）。</summary>
    /// <param name="durationMs">
    /// 缓动时长。★ 滚动中换句时用稍短的档 ✓ 时间越长帧数越多、看起来越顺 ✓
    /// </param>
    /// <remarks>
    /// ★ 关键设计（用户要求"达到 60 帧"之后改的 ✗ 原来做不到 60fps 的原因值得留档）：
    ///
    ///   **窗口宽度一次性设到目标值，动画只作用于"内容布局宽度"** ✓
    ///
    ///   为什么：每帧 `SetWindowPos` 改窗口宽度 ⇒ 要同步等 DWM 重新合成分层窗口表面
    ///   ⇒ **帧间隔硬上限 ~22ms（≈45fps）** ✗ —— 这是实测出来的天花板，靠优化渲染无解 ✓
    ///   而每帧的**渲染**只要 0.30ms（[trace] 实测 ✓）⇒ 把动画挪到"内容重排"就能轻松跑满 60fps ✓
    ///
    ///   代价：窗口先变宽、内容随后跟上 ⇒ 右边会短暂多出一片空白 ✓
    ///   条子的背景面板默认**全透明** ⇒ 看不出来 ✓；面板不透明时观感是"容器先展开、内容跟上" ✓ 也是常见动画 ✓
    /// </remarks>
    private void StartWidthAnimation(int targetWidth, int barHeight, int durationMs = 220)
    {
        _widthAnimFrom = _appliedBarWidthPx;
        _widthAnimTo = targetWidth;
        _widthAnimStart = DateTime.Now;
        _widthAnimDurationMs = Math.Clamp(durationMs, 60, 600);
        _widthAnimTop = _barRect.Top;
        _widthAnimBottom = _barRect.Top + barHeight;

        // ★ 优先级必须是 Render ✗ 默认的 Background 会被渲染/输入压下去 ⇒ 定时器被饿死 ✗
        //   实测症状：120ms 的缓动只跑出 **3 帧**（应该 ~7 帧）、220ms 只跑 6 帧（应该 ~13 帧）✗
        //   3 帧的"动画"看起来就是硬跳 ✓ 等于白做 ✓（与 60fps 的歌词定时器同一套做法 ✓）
        _widthTimer ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _widthTimer.Tick -= OnWidthAnimTick;
        _widthTimer.Tick += OnWidthAnimTick;
        _widthTimer.Start();
    }

    private void OnWidthAnimTick(object? sender, EventArgs e)
    {
        double t = (DateTime.Now - _widthAnimStart).TotalMilliseconds / _widthAnimDurationMs;
        bool done = t >= 1;
        if (done)
        {
            t = 1;
            _widthTimer!.Stop();
        }

        double eased = 1 - Math.Pow(1 - t, 3);   // CubicEaseOut（和侧栏同一曲线）
        int w = (int)Math.Round(_widthAnimFrom + ((_widthAnimTo - _widthAnimFrom) * eased));
        _appliedBarWidthPx = w;
        _barRect.Right = _barRect.Left + w;
        _barRect.Top = _widthAnimTop;
        _barRect.Bottom = _widthAnimBottom;

        // ★ 动画帧走 light：不传 SWP_FRAMECHANGED ✗
        //   否则每帧重建整个分层窗口表面 ⇒ 滚动 + 缓动一起跑就掉帧 ✗
        ApplyBarPosition(0, light: true);
        _widthAnimFrames++;

        if (done)
        {
            Log.Write("layout", $"宽度缓动结束: {_widthAnimFrom} → {_widthAnimTo}px " +
                $"({_widthAnimDurationMs}ms/{_widthAnimFrames} 帧" +
                (_widthAnimRolling ? "，滚动中 ✓" : ", 静止 ✓") + ")");
        }
    }

    private bool _probeDue = true;
    private long _lastProbeAt;

    /// <summary>露出后一段时间内不探测占用区：UIA 会跨进程调 explorer，而它正在做自己的露出动画。</summary>
    private long _suppressProbeUntil;

    /// <summary>上次抬高同级 Z 序的时间。</summary>
    private long _lastRaiseAt;

    /// <summary>隐藏瞬间为"把条按在原位淡出"而施加的临时纵向偏移（客户区坐标）。</summary>
    private int _holdOffsetY;

    /// <summary>淡出结束、可以恢复窗口几何的时刻。</summary>
    private long _fadeOutDoneAt;

    /// <summary>上次轮询设置文件的时刻。</summary>
    private long _lastSettingsPollAt;

    /// <summary>上次记录控件矩形的条宽（避免每帧写日志）。</summary>
    private int _lastLoggedBarWidth = -1;

    /// <summary>数据拉取节拍（参照实现的 provider 轮询节拍：100 ms）。</summary>
    private const long TimelinePollMilliseconds = 100;

    /// <summary>进度重画节拍（参照实现的 <c>_progressTimer</c>：250 ms，每拍无条件写进度值）。</summary>
    private const long ProgressRepaintMilliseconds = 250;

    /// <summary>滚轮手势/切歌的提示窗（懒创建，复用同一实例）。</summary>
    private Components.TooltipWindow? _tooltip;

    /// <summary>跑马灯的 60fps 重画定时器（只在长行滚动时运行）。</summary>
    private DispatcherTimer? _lyricsTimer;

    private readonly bool _lyricsTrace = Environment.GetEnvironmentVariable("RYUBAR_LYRIC_TRACE") == "1";
    private long _traceSince = Environment.TickCount64;
    private int _traceFrames;

    private DispatcherTimer? _timelineTimer;
    private DispatcherTimer? _progressTimer;

    /// <summary>state.json 的例行刷新间隔（另外状态变化时也会立即写）。</summary>
    private const long StateWriteMilliseconds = 5000;

    private long _lastStateWriteAt;

    private double _scale = 1.0;

    private RECT _lastClient;
    private IReadOnlyList<OccupiedRange> _occupied = Array.Empty<OccupiedRange>();
    private string _probeSource = "";
    private string _probeDetail = "";
    private string _reason = "init";
    private RECT _barRect;

    public TaskbarBarWindow(Configuration.SettingsStore settings, Media.MediaSessionWatcher media)
    {
        _settings = settings;
        _media = media;
        InitializeComponent();

        Bar.Clicked += OnBarClicked;
        Bar.SeekRequested += OnSeekRequested;
        Bar.Wheeled += OnWheeled;
        Bar.LyricClicked += OnLyricClicked;              // 单击歌词 → 二级菜单（阶段 B）
        Bar.LyricDoubleClicked += OnLyricDoubleClicked;  // 双击歌词 → 激活播放源（已完成 ✓）
        _media.Changed += OnMediaChanged;
        Bar.Update(_media.Snapshot);

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;

        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(TickMilliseconds),
        };
        _timer.Tick += OnTick;

        _probe.Completed += OnProbeCompleted;

        // 命令行要求打开设置窗口（以后托盘右键也走这里）
        if (App.SettingsRequest.Length > 0)
        {
            // ★ 这里的优先级必须是 Normal ✗ 原来写的是 ApplicationIdle（最低优先级）
            //   ApplicationIdle 只在消息队列**真的空了**才执行，而本进程同时跑着
            //   150ms 落位 + 100ms 时间轴 + 16ms 歌词重绘三套定时器 → 队列几乎从不空 ✗
            //   实测后果：`RyuBar.exe --settings` 永远打不开设置窗口 ✗
            //   （日志里只有"启动完成"，没有"打开设置窗口失败"，也没有任何窗口 → 请求被无限排队）
            Dispatcher.BeginInvoke(new Action(() => OpenSettings(App.SettingsRequest)), DispatcherPriority.Normal);
        }
    }

    /// <summary>打开设置窗口。<paramref name="skin"/> = self / stock / both（both 用于并排对比）。</summary>
    public void OpenSettings(string skin)
    {
        try
        {
            _settings.Changed -= OnSettingsChangedForReapply;
            _settings.Changed += OnSettingsChangedForReapply;

            System.Windows.Rect work = SystemParameters.WorkArea;

            // 默认（--settings 或托盘）：**进程内**的正式设置窗口 —— 自带 Fluent 主题 + 自绘行构件
            if (skin.Equals("theme", StringComparison.OrdinalIgnoreCase)
                || skin.Equals("default", StringComparison.OrdinalIgnoreCase)
                || skin.Equals("glass", StringComparison.OrdinalIgnoreCase))
            {
                Settings.SettingsWindowTheme themed = new(_settings);
                themed.Left = work.Left + 120;
                themed.Top = work.Top + 70;
                themed.Show();
                themed.Activate();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write("settings", "打开设置窗口失败", ex);
        }
    }

    /// <summary>设置变了要立刻重画（含重新落位），这样"改一下马上就能看到"。</summary>
    private void OnSettingsChangedForReapply()
    {
        _needsPlace = true;
        Bar.InvalidateVisual();
        UpdateLyricsView();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource? source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        Attach();
        _timer.Start();
        EnsureProgressTimers();
        EnsureLyricsTimer();
        Lyrics.LyricsService.WarmUp();   // 后台预热歌词连接池（冷连接 ~700ms → 热连接 ~100ms）
    }

    /// <summary>通知持有方：本窗口已不可用，需要重建。只会通知一次。</summary>
    private void RaiseInvalidated(string reason)
    {
        if (_invalidated)
        {
            return;
        }
        _invalidated = true;
        Log.Write("host", $"宿主不可用（{reason}），请求重建");
        _timer.Stop();
        _timelineTimer?.Stop();
        _progressTimer?.Stop();
        HostInvalidated?.Invoke();
    }

    /// <summary>
    /// 托盘「设置」/ 双击 → 启动独立的 WinUI 3 设置程序。
    /// 查找顺序：程序同目录 → 同目录 settings 子目录 → 开发目录。
    /// </summary>
    /// <summary>设置窗口（**同一个进程内**，不启动任何外部 exe）。</summary>
    private Settings.SettingsWindowTheme? _settingsWindow;

    /// <summary>
    /// 托盘「设置」/ 双击 → 打开**进程内**的设置窗口。
    /// 已经是打开状态就直接激活，不重复开。
    /// </summary>
    private void OpenSettingsFromTray()
    {
        try
        {
            if (_settingsWindow is { IsLoaded: true })
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new Settings.SettingsWindowTheme(_settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
            Log.Write("tray", "已打开设置窗口（进程内）");
        }
        catch (Exception ex)
        {
            Log.Write("tray", "打开设置窗口失败", ex);
        }
    }

    private void ExitFromTray()
    {
        Log.Write("tray", "托盘退出");
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>托盘图标（右键：设置 / 退出；双击：设置）。</summary>
    private Interop.TrayIcon? _tray;

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _timelineTimer?.Stop();
        _progressTimer?.Stop();
        _tooltip?.Close();
        _probe.Dispose();
        _tray?.Dispose();
    }

    // ---------------------------------------------------------------- 挂载

    private void Attach()
    {
        // 托盘图标：挂载一次即可（explorer 重启时在 WndProc 里重新 Add）
        if (_tray == null && _hwnd != IntPtr.Zero)
        {
            try
            {
                _tray = new Interop.TrayIcon(_hwnd, "RyuBar — 右键打开设置");
                Log.Write("tray", "托盘图标已添加");
            }
            catch (Exception ex)
            {
                Log.Write("tray", "添加托盘图标失败", ex);
            }
        }

        IntPtr taskbar = TaskbarLocator.FindPrimary();
        if (taskbar == IntPtr.Zero)
        {
            Log.Write("host", "找不到任务栏，稍后重试");
            return;
        }

        _taskbar = taskbar;

        long style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        style = (style & ~NativeMethods.WS_POPUP) | NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, unchecked((int)style));

        long exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle = (exStyle | NativeMethods.WS_EX_NOACTIVATE) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, unchecked((int)exStyle));

        _ = NativeMethods.SetParent(_hwnd, taskbar);

        _attached = true;
        _frozen = true;
        _needsPlace = true;
        _probeDue = true;
        _motion.Reset();
        _lastClient = default;

        Log.Write("host", $"已挂载: taskbar=0x{taskbar.ToInt64():X} class={TaskbarLocator.ClassNameOf(taskbar)} " +
                          $"ex=0x{exStyle:X} style=0x{style:X}");

        SetTickMilliseconds(TickMilliseconds);
        Opacity = 0;

        // 窗口现在就是条本身，"非条部分让给任务栏"由分层窗口的逐像素透明天然完成，
        // 这里把历史上可能设过的窗口区域清掉（NULL = 无区域 = 完整矩形）。
        _ = NativeMethods.SetWindowRgn(_hwnd, IntPtr.Zero, true);

        Place();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        _selfHidden = false;
        StartFadeIn();

        StartFadeTestLoopIfRequested();
    }

    /// <summary>
    /// 诊断用：RYUBAR_FADE_TEST=&lt;毫秒&gt; 时，每隔该毫秒数重放一次淡入。
    /// 用途是隔离测量 —— 任务栏静止、只有我们的淡入在动，从而判断
    /// "淡入卡顿"是出在我们这里，还是出在任务栏自己的露出动画上。默认关闭。
    /// </summary>
    private void StartFadeTestLoopIfRequested()
    {
        string? value = Environment.GetEnvironmentVariable("RYUBAR_FADE_TEST");
        if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value, out int period) || period < 200)
        {
            return;
        }

        Log.Write("fade", $"诊断模式：每 {period} ms 重放一次淡入");
        DispatcherTimer loop = new(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(period),
        };
        loop.Tick += (_, _) => StartFadeIn();
        loop.Start();
    }

    private void TryReattach()
    {
        if (!_attached)
        {
            Attach();
            return;
        }
        IntPtr current = TaskbarLocator.FindPrimary();
        if (current != IntPtr.Zero && current != _taskbar)
        {
            Log.Write("host", $"任务栏句柄变化: 0x{_taskbar.ToInt64():X} → 0x{current.ToInt64():X}，重新挂载");
            _attached = false;
            _taskbar = IntPtr.Zero;
            Attach();
        }
    }

    // ---------------------------------------------------------------- 主循环

    private void OnTick(object? sender, EventArgs e)
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
        {
            // 窗口已经没了（典型场景：Explorer 重启连带销毁了我们的子窗口）
            RaiseInvalidated("hwnd-gone");
            return;
        }
        if (!_attached || _taskbar == IntPtr.Zero || !NativeMethods.IsWindow(_taskbar))
        {
            TryReattach();
            return;
        }

        TaskbarSample sample = TaskbarSampler.Take();
        if (!sample.Found)
        {
            return;
        }
        if (sample.Handle != _taskbar)
        {
            TryReattach();
            return;
        }

        _scale = sample.Scale;

        // 任务栏自动隐藏：宿主不隐藏（它跟着父窗口一起离屏），只把透明度归零。
        // 这样任务栏露出的那一帧我们已经在场，可以在同一帧开始淡入 —— 不再迟到。
        if (sample.Hidden)
        {
            if (!_taskbarHidden)
            {
                _taskbarHidden = true;
                _motion.Reset();
                SetTickMilliseconds(HiddenTickMilliseconds);
                _reason = "taskbar-hidden(host-kept)";
                _tooltip?.HideNow();      // 条都藏了，提示不该孤零零留在屏幕上;

                if (StartFadeOutOnHide(sample))
                {
                    Log.Write("fade", "任务栏隐藏：宿主保留，抵消父窗口位移后在原位淡出");
                }
                else
                {
                    CancelFade();
                    Log.Write("fade", "任务栏隐藏：宿主保留并离屏（淡出未开启）");
                }
                WriteState(sample);
            }

            // 淡出结束：把窗口几何恢复成正常的客户区坐标（此时它在屏幕外，看不见）
            if (_holdOffsetY != 0 && Environment.TickCount64 >= _fadeOutDoneAt)
            {
                _holdOffsetY = 0;
                CancelFade();
                ApplyBarPosition(0);
            }

            _frozen = true;
            return;
        }

        if (_taskbarHidden)
        {
            _taskbarHidden = false;
            SetTickMilliseconds(TickMilliseconds);
            _motion.Reset();
            _frozen = true;

            // 若隐藏时施加过"按在原位"的偏移，这里先归零
            if (_holdOffsetY != 0)
            {
                _holdOffsetY = 0;
                ApplyBarPosition(0);
            }

            // 自动隐藏只是父窗口平移，客户区尺寸没变，我们的布局（客户区坐标）本来就是对的。
            // 所以这里**不重新落位、不重新探测**：SetWindowPos + SetWindowRgn 会重建分层表面，
            // UIA 探测会抢 CPU —— 两件事都恰好落在淡入中途，就是那句话说的"卡一下"。
            bool geometryChanged = !sample.ClientScreenRect.SameAs(_lastClient);
            _needsPlace = geometryChanged;
            _probeDue = geometryChanged;
            _suppressProbeUntil = Environment.TickCount64 + ProbeSuppressAfterRevealMilliseconds;
            _reason = geometryChanged ? "revealing-relayout" : "revealing";
            RaiseAboveSiblings();
            _lastRaiseAt = Environment.TickCount64;
            ScheduleFadeIn();
            return;
        }

        bool stable = _motion.Observe(sample.WindowRect);
        if (!stable)
        {
            // 运动冻结：不动几何、不请求探测，等它停下
            _frozen = true;
            return;
        }

        if (_frozen)
        {
            _frozen = false;
            if (_needsPlace)
            {
                // 只在几何真的变了（分辨率/DPI/任务栏尺寸）时才重设窗口位置与区域
                Place();
                _needsPlace = false;
            }
            ShowHost("reveal");
            return;
        }

        if (!sample.ClientScreenRect.SameAs(_lastClient))
        {
            // 几何变化（分辨率/DPI/任务栏尺寸）：重新探测占用区，避免用过期的数据落位
            _probeDue = true;
            _needsPlace = true;
        }

        if (_needsPlace)
        {
            Place();
            _needsPlace = false;
        }

        // 定期抬到同级窗口最上面：Shell 重排同级窗口时不至于被内容桥盖住
        long now = Environment.TickCount64;
        if (now - _lastRaiseAt >= RaiseIntervalMilliseconds)
        {
            _lastRaiseAt = now;
            RaiseAboveSiblings();
        }

        // 设置文件被外部改动后就地生效（改完不用重启）
        if (now - _lastSettingsPollAt >= SettingsPollMilliseconds)
        {
            _lastSettingsPollAt = now;
            _settings.PollForChanges();
        }

        // 进度与媒体的节奏**不在这里**：参照实现用的是两个独立定时器
        // （数据 100 ms / 重画 250 ms），见 EnsureProgressTimers()。
        // 采样循环只管窗口几何与占用区。
        if (now - _lastStateWriteAt >= StateWriteMilliseconds)
        {
            _lastStateWriteAt = now;
            WriteState(sample);
        }

        MaybeProbe(sample);
    }

    /// <summary>
    /// 按参照实现的结构建两个独立定时器（幂等，挂载后调用）。
    ///
    ///   * <c>_timelineTimer</c> 100 ms —— 拉数据（参照实现的 NetEase/KuGou provider 就是这个节拍，
    ///     它们的 SMTC 侧则靠事件 + 1 秒看门狗）；
    ///   * <c>_progressTimer</c> 250 ms —— **无条件**重画进度（参照实现的 <c>_progressTimer</c> 每拍都写进度值）。
    ///
    /// 刻意与窗口采样循环分开：那个循环只在任务栏稳定态跑，节奏会被它带偏；分开后两者各自固定。
    /// </summary>
    private void EnsureProgressTimers()
    {
        if (_timelineTimer == null)
        {
            _timelineTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(TimelinePollMilliseconds),
            };
            _timelineTimer.Tick += (_, _) =>
            {
                if (_attached && !_selfHidden)
                {
                    _media.Poke();
                }
            };
            _timelineTimer.Start();
        }

        if (_progressTimer == null)
        {
            _progressTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ProgressRepaintMilliseconds),
            };
            _progressTimer.Tick += (_, _) =>
            {
                if (_attached && !_selfHidden && _media.Snapshot.HasSession)
                {
                    Bar.InvalidateVisual();
                    UpdateLyricsView();   // 歌词行随播放位置推进（行边界切换就靠这里）
                    TryFlushPendingNotification(_media.Snapshot);   // 兜底：封面到位后补发通知
                }
            };
            _progressTimer.Start();
        }
    }

    /// <summary>
    /// 是否需要做一次 UIA 探测。
    ///
    /// UIA 是跨进程 COM 调用，很贵（实测它是"任务栏可见"时 CPU 的主要来源）。
    /// 而占用区只在**任务栏按钮集合变化**时才会变，所以：
    ///   * 每 500 ms 用一个很便宜的顶层窗口指纹判断"按钮集合变了吗"（一次 EnumWindows）；
    ///   * 变了才探；没变就只在兜底周期（15 秒）探一次。
    /// </summary>
    private void MaybeProbe(in TaskbarSample sample)
    {
        long now = Environment.TickCount64;
        if (now < _suppressProbeUntil)
        {
            return;
        }

        bool fingerprintDue = now - _lastFingerprintAt >= FingerprintIntervalMilliseconds;
        bool fingerprintChanged = false;
        if (fingerprintDue)
        {
            _lastFingerprintAt = now;
            int signature = ComputeWindowSignature();
            fingerprintChanged = signature != _lastWindowSignature;
            _lastWindowSignature = signature;
        }

        bool due = _probeDue
            || fingerprintChanged
            || now - _lastProbeAt >= ProbeSafetyMilliseconds;
        if (!due)
        {
            return;
        }

        _lastProbeAt = now;
        _probeDue = false;
        _probe.Request(sample.Handle, TaskbarLocator.ProcessIdOf(sample.Handle), sample.ClientHeight, sample.Scale);
    }

    /// <summary>
    /// 任务栏按钮集合的廉价指纹：可见的、有标题的顶层窗口的句柄哈希。
    /// 不做任何跨进程调用，一次枚举约 1 ms。
    /// </summary>
    private static int ComputeWindowSignature()
    {
        int hash = 17;
        int count = 0;
        _ = NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }
            if (NativeMethods.GetWindowTextLength(hwnd) == 0)
            {
                return true;
            }
            unchecked
            {
                hash = hash * 31 + (int)hwnd.ToInt64();
            }
            count++;
            return true;
        }, IntPtr.Zero);
        unchecked
        {
            return hash * 31 + count;
        }
    }

    private void OnProbeCompleted(ProbeResult result)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _probeSource = result.Source;
            _probeDetail = result.Detail;
            if (result.Source == "menu-open")
            {
                // 菜单打开时整棵树被换成菜单子树，用它算布局会把条挤走；保留上次布局
                Log.Write("probe", "菜单打开，作废本次探测");
                return;
            }
            if (result.Source == "error")
            {
                Log.Write("probe", "探测失败，保留上次布局: " + result.Detail);
                return;
            }

            bool changed = !SameRanges(_occupied, result.Ranges);
            _occupied = result.Ranges;
            if (changed)
            {
                _needsPlace = true;
                Log.Write("probe", $"{result.Source} ranges={result.Ranges.Count} {result.Detail}");
            }
        }));
    }

    private static bool SameRanges(IReadOnlyList<OccupiedRange> a, IReadOnlyList<OccupiedRange> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Left != b[i].Left || a[i].Right != b[i].Right)
            {
                return false;
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- 布局

    private void Place()
    {
        TaskbarSample sample = TaskbarSampler.Take();
        if (!sample.Found)
        {
            return;
        }

        _scale = sample.Scale;
        _lastClient = sample.ClientScreenRect;

        int edge = (int)Math.Round(EdgePaddingDip * _scale);
        int gap = (int)Math.Round(GapDip * _scale);
        int top = (int)Math.Round(TopMarginDip * _scale);

        // 「安全区」：宽度策略来自设置
        //   auto  → 想要的宽度 = 内容自适应宽度
        //   fixed → 想要的宽度 = 用户指定值
        //   minimum = 可接受的最小宽度：开启"空档不够时压缩"时就是下限（策略会自动瘦身 ✓ 不会直接消失 ✓），
        //             关闭时等于想要的宽度（保持老行为：放不下就隐藏 ✓）
        Configuration.BarSettings barSettings = _settings.Current.Bar;
        double floorDip = Math.Max(160, barSettings.MinimumWidthDip);
        // 固定宽度就按用户设的值（设置界面已把可调下限卡在"歌词区最小宽度 + 其余部件"上 ✓
        // 所以不需要宿主再按内容宽度抬高它 ✗ —— 那样会让用户设 400 却得到 557）
        // ★ 空闲态（没有音源）：**不套最小宽度下限** ✗ 条子直接等于内容宽度（= 只有封面）✓
        //   用户实测反馈：只把视图里的文字宽度归 0 是不够的 ✗
        //   宿主这边还有 floorDip（默认 160 DIP）⇒ 条子仍被撑成一根空条子，左边挂个音符，很难看 ✗
        double wantedDip = Bar.IsIdle
            ? Math.Max(48, Bar.BarWidth)   // 留一个下限只为避免极端情况下的 0 宽度 ✓
            : barSettings.WidthMode == "fixed"
                ? Math.Max(floorDip, barSettings.WidthDip)
                : Math.Max(floorDip, Bar.BarWidth);

        // ★ 自动模式下"内容想变的宽度"可能**超过空档** ✗
        //   原因：歌词自动适配的上限 = 最大限位（机器能达到的极限，用户要求 ✓）→ 不受当前配置约束 ✓
        //   如果不管：wantedDip 会大于空档 → 关掉"空档不够时压缩"时 minimum = wantedDip
        //   → RangePolicy 判定 no-room → **整条消失** ✗（这正是用户踩过的陷阱 ✗）
        //   所以先钳到"当前配置真正放得下"的宽度 ✓ 条子绝不会因为歌词变长而消失 ✓
        //   （值来自上一轮实测：滞后一帧 150ms，无感 ✓）
        if (barSettings.WidthMode != "fixed" && _liveTextWidthDip > 0)
        {
            wantedDip = Math.Min(wantedDip, Math.Max(floorDip, _liveTextWidthDip + Bar.ChromeWidthDip));
        }

        int desired = (int)Math.Round(wantedDip * _scale);
        int minimum = (int)Math.Round((barSettings.ShrinkWhenTight ? floorDip : wantedDip) * _scale);
        int barHeight = Math.Max(1, sample.ClientHeight - 2 * top);

        List<OccupiedRange> local = new(_occupied.Count);
        foreach (OccupiedRange range in _occupied)
        {
            local.Add(new OccupiedRange(
                range.Left - sample.ClientScreenRect.Left,
                range.Right - sample.ClientScreenRect.Left,
                range.Name));
        }

        RangePolicy.Result choice = RangePolicy.Choose(local, sample.ClientWidth, desired, edge, gap, minimum);
        string previousReason = _reason;   // 先留一份：下面会把 _reason 覆盖成新结果 ✓
        _reason = choice.Reason;

        if (!choice.Found)
        {
            Log.Write("layout", $"放不下: {choice.Reason} client={sample.ClientWidth} occupied={_occupied.Count} source={_probeSource}");
            HideHost($"no-room:{choice.Reason}");
            return;
        }

        _barRect = new RECT
        {
            Left = choice.Left,
            Top = top,
            Right = choice.Left + choice.Width,
            Bottom = top + barHeight,
        };

        // ★ 宽度**缓动**（用户："歌词宽度变化时很生硬…想让他们有个动画" ✓）
        // 做法和侧栏那套一致 ✓：DispatcherTimer(16ms) 插值 220ms + CubicEaseOut ✓
        // 阈值：变化 ≥ 8 DIP 才动画 ✓ —— 每句的微小差异直接跳 ✓ 否则会"一直晃" ✗
        // 不在固定宽度模式下动画 ✓（那时宽度本来就是用户定的 ✓ 不该动 ✓）
        // 拖动进度条时不打断 ✓（宽度变化与本条无关 ✓）
        // 诊断：RYUBAR_WIDTH_TRACE=1 → 死区压到 1 DIP（任何宽度变化都走动画 ✓ 专门用来测帧数 ✓）
        // 为什么需要它：真实换句里多数变化小于 8 DIP 死区 ⇒ 直接跳 ⇒ 抓不到缓动 ✗
        int deadZone = Environment.GetEnvironmentVariable("RYUBAR_WIDTH_TRACE") == "1"
            ? 1
            : (int)Math.Max(4, Math.Round(8 * (_scale <= 0 ? 1 : _scale)));
        bool rolling = Bar.IsLyricsRolling;

        // ★ 滚动中也**做缓动** ✓（原来这里跳过缓动 ✗ 见下面说明）
        //   当初跳过的理由是"两者同时跑会掉帧"✓ 但那个结论的**真凶已经找到并修掉了**：
        //   动画每一帧的 SetWindowPos 都带了 SWP_FRAMECHANGED ✗ ⇒ 每帧重建整个分层窗口表面 ✗
        //   现在动画帧走 light（不带该标志）✓ 滚动 + 缓动可以同时跑 ✓
        //   —— 这也正是用户要的："换位置时要有动画"
        //      （频谱就在歌词右侧 ✓ 歌词一变宽，频谱的位置就跟着平移 ✓ 没有缓动就是硬跳 ✗）
        bool wantAnim = barSettings.WidthMode != "fixed"
            && _appliedBarWidthPx > 0
            && Math.Abs(choice.Width - _appliedBarWidthPx) >= deadZone;

        if (wantAnim)
        {
            // 滚动中单帧成本更高 ⇒ 更短的缓动（120ms）✓ 静止时仍是 220ms ✓
            _widthAnimRolling = rolling;
            _widthAnimFrames = 0;
            StartWidthAnimation(choice.Width, barHeight, rolling ? 180 : 220);
        }
        else if (_appliedBarWidthPx > 0 && Math.Abs(choice.Width - _appliedBarWidthPx) > 0)
        {
            Log.Write("layout", $"宽度变化 {_appliedBarWidthPx} → {choice.Width}px 小于阈值 {deadZone}px，直接跳 ✓");
        }
        else
        {
            _widthTimer?.Stop();
            _appliedBarWidthPx = choice.Width;
            _barRect.Right = choice.Left + choice.Width;
            _barRect.Bottom = top + barHeight;
            ApplyBarPosition(0);
        }

        // ① 策略可能把宽度**压缩**过（clamped）：必须把这个真实宽度告诉视图 ✗
        //    否则视图还按用户设的固定宽度画 → 看起来是"被裁掉"而不是"被压缩"（用户实测反馈"压缩不生效"）。
        //    ★ 这是**唯一**写强制宽度的地方，并记进 _appliedForcedWidthDip 供 ReapplySettings 复用，
        //      避免两处各写一个值导致"自适应↔固定"来回切换（抽搐的真因）。
        if (barSettings.WidthMode == "fixed")
        {
            double viewScale = _scale <= 0 ? 1.0 : _scale;
            _appliedForcedWidthDip = (int)Math.Round(choice.Width / viewScale);
        }
        else
        {
            _appliedForcedWidthDip = 0;
        }

        // ★ 宽度动画进行中**不要覆盖**视图宽度 ✗
        //   动画每帧在给视图喂插值宽度 ✓ 这里覆盖会让它跳回去 ⇒ 动画被反复打断 ✗
        if (_widthTimer?.IsEnabled != true)
        {
            Bar.SetForcedWidthDip(_appliedForcedWidthDip);
        }

        // ③ 歌词**自动适配**模式的文字区上限 = "任务栏最多能给媒体条多宽" − 除文字区以外的部件宽度 ✓
        //    为什么在这里算：只有宿主知道任务栏的实际占用 ✗ 视图不知道 ✓
        //    ★ 用 RangePolicy.MaxUsableWidth（**和落位策略同一套空闲段算法**）✗ 不要自己乘系数估
        //      —— 估算会得到"上限说能放下、实际被压缩到更窄"的不一致 ✗ 用户看到的就是"设了没用" ✗
        //    ★ 与文字区宽度无关（只跟图标占用 + 部件开关有关）→ 本值稳定 ✓ 不会和宽度缓动互相追着跑 ✓
        int usablePx = RangePolicy.MaxUsableWidth(local, sample.ClientWidth, edge, gap);
        double capScale = _scale <= 0 ? 1.0 : _scale;
        // ★ 探针还没采到任务栏图标时（_occupied 为空）**不能**用这个值 ✗
        //   那时"空闲段"= 整条任务栏（2544px → 1696 DIP）→ 上限会是个荒谬的大数 ✗
        //   实测过：探针就绪前那个值算出来是 1555 → 设置界面把它 clamp 成 800 ✗
        //   于是"580 也合法"✗ → 防呆整个失效（用户设多少都"看起来"合法，实际被落位策略压缩）
        //   所以探针未就绪时写 0 = "未知" ✓ 视图退回保守上限 ✓ 设置界面也不做钳制 ✓
        if (_occupied.Count > 0)
        {
            double usableDip = usablePx / capScale;
            _usableDip = (int)Math.Round(usableDip);
            _chromeDip = Bar.ChromeWidthDip;
            _minChromeDip = Bar.MinChromeWidthDip;

            // ① **最大限位** = 空档 − 必需部件（留白 + 封面 + 时间）
            //    用户要求"歌词宽度自动适配开启时，最大限位 = 当前能调整的最大值" ✓
            //    ⇒ 它同时也当「歌词自定义宽度」输入框的可调上限（两处同一个数，不会再出现两套上限 ✓）
            //    所以它**不跟着可选部件浮动** ✗ 它代表这台机器能达到的最大值
            //    （把频谱 / 控制按钮 / 进度条全关掉时的上限）✓
            _maxTextWidthDip = (int)Math.Round(usableDip - Bar.MinChromeWidthDip);

            // ② 当前配置**实际**能放多少 = 空档 − 当前 chrome（只用于提示条说明 + 防"条子消失"，不当上限 ✗）
            _liveTextWidthDip = (int)Math.Round(usableDip - Bar.ChromeWidthDip);
        }
        else
        {
            _usableDip = 0;
            _chromeDip = 0;
            _minChromeDip = 0;
            _maxTextWidthDip = 0;
            _liveTextWidthDip = 0;
        }

        // ★ 自动适配的上限 = **最大限位**（用户要求 ✓）
        //   注意代价：内容想变到上限宽度时，整条会超过空档 → 由下面 Place 的钳制负责"别撑爆/别消失" ✓
        Bar.SetAutoFitWidthCapDip(_maxTextWidthDip);

        // ② 之前因为"没地方放"隐藏过 → 现在找到位置了就**自动恢复显示** ✓
        //    注意必须用**覆盖前**的 reason 判断：上面刚把 _reason 改成 "fit"，
        //    如果拿新的判断，条件永远为假（实测踩到，只能重启程序才能回来）。
        if (_selfHidden && previousReason.StartsWith("no-room", StringComparison.Ordinal))
        {
            ShowHost("room-again");
        }

        WriteState(sample);
    }

    /// <summary>
    /// 真正隐藏宿主。只用于"没地方放"和 Explorer 恢复：这两种情况下条不该占着任务栏。
    /// 自动隐藏走的是另一条路（保留宿主、只把透明度归零）。
    /// </summary>
    private void HideHost(string reason)
    {
        if (!_selfHidden)
        {
            Log.Write("layout", $"隐藏宿主: {reason}");
        }
        CancelFade();
        _selfHidden = true;
        _reason = reason;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        WriteState(TaskbarSampler.Take());
    }

    // ---------------------------------------------------------------- 媒体与交互

    /// <summary>媒体快照变化：更新内容并重新落位（标题长短会改变条的宽度）。</summary>
    private void OnMediaChanged(Media.MediaSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnMediaChanged(snapshot)));
            return;
        }
        Bar.Update(snapshot);
        _needsPlace = true;

        // ★ 面板开着就同步刷新 ✗（用户报告：播放按钮不随状态变、进度不走 ✓ 根因就是这里没更新 ✓）
        if (_servicePanel is { IsVisible: true })
        {
            _servicePanel.Update(snapshot, _appVolume);
        }

        MaybeNotifyTrackChange(snapshot);
        TryFlushPendingNotification(snapshot);   // 封面到了就补发通知
    }

    /// <summary>
    /// 切歌通知：曲目（曲名 + 歌手）变化时弹一次图文通知（封面 + 曲名 + 歌手·来源）。
    /// 启动后第一次拿到快照不算切歌（否则每次启动都会弹一下）。
    /// </summary>
    private void MaybeNotifyTrackChange(Media.MediaSnapshot snapshot)
    {
        if (!snapshot.HasSession || string.IsNullOrWhiteSpace(snapshot.Title))
        {
            return;
        }

        string key = snapshot.Title + "\u001f" + snapshot.Artist;
        if (_lastTrackKey == null)
        {
            _lastTrackKey = key;   // 首次只记录，不弹
            return;
        }
        if (key == _lastTrackKey)
        {
            return;
        }
        _lastTrackKey = key;

        Configuration.NotificationSettings settings = _settings.Current.Notification;
        if (!settings.Enabled)
        {
            return;
        }

        // 封面是异步解码的，切歌那一帧通常还没有 → 先记成待发，等它到了再弹（最多等 1.5 秒）
        _pendingNotifyKey = key;
        _pendingNotifyAt = Environment.TickCount64;
        TryFlushPendingNotification(snapshot);
    }

    /// <summary>待发通知：等新封面解码到位再弹，超时就按无封面弹出。</summary>
    private void TryFlushPendingNotification(Media.MediaSnapshot snapshot)
    {
        if (_pendingNotifyKey == null)
        {
            return;
        }

        if (snapshot.Title + "\u001f" + snapshot.Artist != _pendingNotifyKey)
        {
            _pendingNotifyKey = null;   // 又换歌了，旧通知作废
            return;
        }

        bool hasArtwork = snapshot.Artwork != null;
        bool timedOut = Environment.TickCount64 - _pendingNotifyAt > 1500;
        if (!hasArtwork && !timedOut)
        {
            return;                     // 再等下一帧
        }

        _pendingNotifyKey = null;
        Configuration.NotificationSettings settings = _settings.Current.Notification;
        if (!settings.Enabled)
        {
            return;
        }

        string source = Media.SourceNames.Friendly(snapshot.Source, "");
        string detail = string.IsNullOrWhiteSpace(snapshot.Artist)
            ? source
            : snapshot.Artist + (string.IsNullOrWhiteSpace(source) ? "" : " · " + source);

        ShowTooltip(
            snapshot.DisplayTitle,
            detail,
            settings.Milliseconds,
            settings.ShowArtwork ? snapshot.Artwork : null,
            settings.Position);
    }

    private string? _lastTrackKey;
    private string? _pendingNotifyKey;
    private long _pendingNotifyAt;

    private void OnBarClicked(Components.BarHit hit)
    {
        Log.Write("input", $"点击 {hit}");
        switch (hit)
        {
            case Components.BarHit.PlayPause:
            case Components.BarHit.Artwork:      // 阶段 2 先用封面当播放/暂停，之后再做"激活来源"
                _ = _media.TogglePlayPauseAsync();
                break;
            case Components.BarHit.Previous:
                _ = _media.SkipPreviousAsync();
                break;
            case Components.BarHit.Next:
                _ = _media.SkipNextAsync();
                break;
        }
    }

    /// <summary>
    /// 歌词区**单击** → 在条子**上方**调出二级面板（用户要求 ✓ 进度条 + 单应用音量 ✓）。
    /// ★ 面板必须是独立顶层窗口 ✗ 条子是 Shell_TrayWnd 的 WS_CHILD ⇒ 画不出任务栏之外 ✓
    /// </summary>
    private void OnLyricClicked()
    {
        // ★ 再点一次就关 ✗（用户："二级菜单的窗口打开后可以通过再次单击歌词关闭"✓）
        if (_servicePanel is { IsVisible: true })
        {
            Log.Write("input", "歌词单击 → 关闭二级面板");
            _servicePanel.Hide();
            return;
        }

        Log.Write("input", "歌词单击 → 打开二级面板");
        _servicePanel ??= CreateServicePanel();
        _servicePanel.ShowAbove(BarScreenRect(), _scale, _media.Snapshot, _appVolume);
    }

    /// <summary>
    /// 条子在**屏幕坐标系**里的矩形。
    /// ★ `_barRect` 是**相对任务栏**的坐标 ✗（Place 里算的是子窗口在父窗口客户区内的位置 ✓）
    ///   直接拿它当屏幕坐标用，面板就会跑到屏幕左上角去 ✓（用户实测"窗口位置错误"✓）
    /// </summary>
    private Rect BarScreenRect()
    {
        if (_taskbar == IntPtr.Zero || !NativeMethods.GetWindowRect(_taskbar, out Interop.RECT taskbar))
        {
            return new Rect(_barRect.Left, _barRect.Top, _barRect.Width, _barRect.Height);
        }

        return new Rect(
            taskbar.Left + _barRect.Left,
            taskbar.Top + _barRect.Top,
            _barRect.Width,
            _barRect.Height);
    }

    private Menu.ServicePanel CreateServicePanel()
    {
        Menu.ServicePanel panel = new();
        panel.SeekRequested += OnSeekRequested;
        panel.CommandInvoked += OnBarClicked;
        panel.VolumeChanged += OnPanelVolumeChanged;
        panel.MuteToggled += OnPanelMuteToggled;
        return panel;
    }

    /// <summary>面板里的音量滑块（0..1）—— 只调**当前播放源那个应用**的会话音量 ✓。</summary>
    private void OnPanelVolumeChanged(double volume)
    {
        _appVolume = Math.Clamp(volume, 0, 1);
        Log.Write("input", $"面板音量 → {Math.Round(_appVolume * 100)}%（单应用，待接入 WASAPI 会话）");
    }

    private void OnPanelMuteToggled(bool muted)
    {
        Log.Write("input", $"面板静音 → {muted}（待接入 WASAPI 会话）");
    }

    /// <summary>当前应用音量（0..1）。没有 WASAPI 会话支持时保持 1 ✓ 面板仍可拖动但不生效 ✓</summary>
    private double _appVolume = 1.0;

    private Menu.ServicePanel? _servicePanel;

    /// <summary>
    /// 歌词区**双击** → 快速打开**当前正在运行的**播放源应用（用户要求 ✓）。
    /// ★ 只激活已运行的实例 ✗ 绝不启动新进程 ✓（所以找不到就什么都不做 ✓）
    /// </summary>
    private void OnLyricDoubleClicked()
    {
        string source = _media.Snapshot.Source;
        Log.Write("input", $"歌词双击 → 激活播放源：'{source}'");

        string? exe = Media.SourceNames.ExecutableName(source);
        if (exe is null)
        {
            Log.Write("input", "  无法从来源标识解析出可执行名（UWP 包等），跳过");
            return;
        }

        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName(exe))
        {
            try
            {
                IntPtr hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                {
                    continue;   // 后台进程 / 没窗口 ⇒ 不是用户想打开的那个 ✓
                }

                // 最小化的先还原 ✓ 再前置 ✓
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(hwnd);
                Log.Write("input", $"  已激活 {process.ProcessName} (pid={process.Id})");
                return;
            }
            catch (Exception exception)
            {
                Log.Write("input", $"  激活 {exe} 失败：{exception.GetType().Name}");
            }
            finally
            {
                process.Dispose();
            }
        }

        Log.Write("input", $"  没有找到带窗口的 {exe} 进程 ⇒ 不启动新实例 ✓");
    }

    private void OnSeekRequested(double ratio)
    {
        Media.MediaSnapshot snapshot = _media.Snapshot;
        if (!snapshot.CanSeek || snapshot.Duration <= TimeSpan.Zero)
        {
            Log.Write("input", "该播放器不支持跳转，忽略进度条操作");
            return;
        }
        TimeSpan target = TimeSpan.FromSeconds(snapshot.Duration.TotalSeconds * Math.Clamp(ratio, 0, 1));
        Log.Write("input", $"跳转到 {target:hh\\:mm\\:ss}（{ratio:0.00}）");
        _ = _media.SeekAsync(target);
    }

    /// <summary>
    /// 滚轮手势。普通滚轮与 Ctrl+滚轮分别可绑（对应参照实现的 primaryWheelAction / chordWheelAction）：
    /// 默认普通滚轮 = 上下首，Ctrl+滚轮 = 循环切换声音来源；切换后弹提示告诉你切到了谁。
    /// </summary>
    private void OnWheeled(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        Configuration.InteractionSettings interaction = _settings.Current.Interaction;
        bool chord = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        Configuration.WheelActionKind kind = chord ? interaction.ChordKind : interaction.PrimaryKind;

        // 滚轮向前（上）取负：与"上 = 上一个"的直觉一致
        int steps = delta > 0 ? -1 : 1;
        Log.Write("input", $"滚轮 delta={delta} 手势={(chord ? "Ctrl+" : "")}动作={kind}");

        switch (kind)
        {
            case Configuration.WheelActionKind.PreviousNext:
                if (steps < 0)
                {
                    _ = _media.SkipPreviousAsync();
                }
                else
                {
                    _ = _media.SkipNextAsync();
                }
                ShowTooltip(steps < 0 ? "上一首" : "下一首", "", interaction.TooltipMilliseconds);
                break;

            case Configuration.WheelActionKind.SwitchSource:
                Media.MediaSourceOption? option = _media.CycleSource(steps);
                ShowTooltip("切换声音来源", option?.DisplayName ?? "只有一个来源", interaction.TooltipMilliseconds);
                break;

            case Configuration.WheelActionKind.None:
            default:
                break;
        }
    }

    /// <summary>
    /// 弹提示（复用一个永不激活的窗口）。落点由 <c>interaction.tooltipPosition</c> 决定：
    /// <c>aboveArtwork</c>（默认，左对齐封面、贴条上方）或参照实现的六个屏幕位置
    /// （<c>topLeft</c> / <c>topCenter</c> / <c>topRight</c> / <c>bottomLeft</c> / <c>bottomCenter</c> / <c>bottomRight</c>）。
    /// </summary>
    /// <summary>跑马灯用的 60fps 重画定时器（只在长行滚动时运行，滚完自己停）。</summary>
    private void EnsureLyricsTimer()
    {
        if (_lyricsTimer != null)
        {
            return;
        }

        _lyricsTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _lyricsTimer.Tick += (_, _) =>
        {
            // ★ 原来这里只判 IsMarqueeActive ✗ → **逐字高亮（karaoke）根本没被驱动** ✗
            // 于是逐字只能靠 250ms 的进度重绘定时器更新 = **4 fps** ✗✓
            // 用户："滚动是有了，但是很卡帧率比较低，帧率我希望有 60 帧（逐字也是）" ✓✓
            // 现在两种都驱动 ✓（60fps ✓）；两种都不需要时再停表 ✓
            if (Bar.IsMarqueeActive || Bar.IsKaraokeActive || Bar.IsLyricsRolling || Bar.IsSpectrumActive)
            {
                PushSpectrumLevels();   // 每帧推一次真实频段（16 个 float ✓ 几乎零成本 ✓）
                Bar.InvalidateVisual();
            }
            else
            {
                _lyricsTimer.Stop();
            }
        };

        // 诊断：RYUBAR_LYRIC_TRACE=1 时统计"渲染真正跑到多少帧"，用来判断滚动是否顺滑
        if (_lyricsTrace)
        {
            System.Windows.Media.CompositionTarget.Rendering += (_, _) =>
            {
                long now = Environment.TickCount64;
                _traceFrames++;
                if (now - _traceSince >= 1000)
                {
                    Log.Write("trace", $"歌词滚动 {_traceFrames} 帧/秒（滚动中={Bar.IsMarqueeActive} 逐字中={Bar.IsKaraokeActive}，宿主可见={!_selfHidden && !_taskbarHidden}）" +
                                       $"  每帧渲染 平均={Bar.AverageRenderMilliseconds:0.00}ms 峰值={Bar.MaxRenderMilliseconds:0}ms" +
                                       $"  {Bar.DescribeLyricsState()}");
                    Bar.ResetRenderStats();
                    _traceFrames = 0;
                    _traceSince = now;
                }
            };
        }
    }

    /// <summary>
    /// 按当前播放位置算好歌词显示数据交给视图（视图只负责画）。
    /// 没有歌词 / 没有会话 → 传 null，视图自动回落到标题/歌手。
    /// </summary>
    private void UpdateLyricsView()
    {
        try
        {
            Media.MediaSnapshot snapshot = _media.Snapshot;
            Lyrics.LyricTrack track = _media.Lyrics.Current;
            Configuration.LyricsSettings settings = _settings.Current.Lyrics;
            int beforeWidth = (int)Bar.BarWidth;

            // 「安全区」强制宽度：**这里绝不能写 0** ✗
            // 之前这里写 0、Place() 写实测值 → 两处每帧抢着设 → 视图在"自适应宽度(557 DIP)"
            // 与"固定宽度"之间来回切换 → 界面抽搐（用户实测：557 以下必现）。
            // 现在两边共用 _appliedForcedWidthDip：只有 Place() 改它，这里只负责重新施加同一个值。
            // ★ 动画进行中不要覆盖视图宽度 ✗（这里每 150ms 都会跑到，覆盖会让动画跳回去 ✗）
            if (_widthTimer?.IsEnabled != true)
            {
                Bar.SetForcedWidthDip(_appliedForcedWidthDip);
            }

            Bar.ApplyLyricsSettings(
                settings.Lines,
                settings.PerWordHighlight,
                settings.Marquee,
                settings.WidthDip,
                settings.TextColor,
                settings.HighlightColor,
                settings.NextLineColor,
                settings.Alignment switch { "center" => 1, "right" => 2, _ => 0 },
                settings.FontScalePercent,
                settings.ArtSizePercent,
                settings.AutoFitWidth);

            // 媒体条构成开关（环形进度 / 条状进度 / 控制按钮 / 环上可拖动）
            Bar.ApplyBarElements(
                _settings.Current.Bar.RingProgress,
                _settings.Current.Bar.BarProgress,
                _settings.Current.Bar.ShowControls,
                _settings.Current.Bar.RingSeek,
                _settings.Current.Bar.RingColor,
                _settings.Current.Bar.RingOpacity,
                _settings.Current.Bar.RingThicknessDip);

            // 媒体条背景面板的不透明度（只动面板 ✓ 内容不变 ✓）
            Bar.ApplyBarAppearance(_settings.Current.Bar.BackgroundOpacity);

            // 频谱（先做样子 ✓ 数据是合成的 ✓）
            Bar.ApplySpectrum(_settings.Current.Bar.Spectrum, _settings.Current.Bar.SpectrumBars);
            EnsureSpectrumAnalyzer();

            // 歌词文字区宽度变了 → 条要重新落位（否则改了设置得等下次换曲才生效）
            if ((int)Bar.BarWidth != beforeWidth)
            {
                _needsPlace = true;
            }

            if (!snapshot.HasSession || !track.HasLines)
            {
                Bar.SetLyrics(null);
                return;
            }

            int positionMs = (int)snapshot.PositionAt(DateTimeOffset.Now).TotalMilliseconds;

            // 只丢"制作信息行"：命中比例过高或过滤后清空时整体放弃过滤（宁多显示一行，也不清空歌词）
            bool[] dropMask = ResolveInfoLineMask(track, settings.FilterInfoLines);
            if (!ReferenceEquals(_maskTrack, track))
            {
                _maskTrack = track;
                int dropped = dropMask.Count(v => v);
                if (dropped > 0 || settings.FilterInfoLines)
                {
                    Log.Write("lyrics", $"信息行过滤: 共 {track.Lines.Count} 行，丢弃 {dropped} 行（开关={settings.FilterInfoLines}）");
                }
            }

            int index = CurrentVisibleIndex(track, dropMask, positionMs);
            if (index < 0)
            {
                Bar.SetLyrics(null);
                return;
            }

            Lyrics.LyricLine current = track.Lines[index];
            int nextIndex = NextVisibleIndex(track, dropMask, index);
            Lyrics.LyricLine? next = nextIndex >= 0 ? track.Lines[nextIndex] : null;
            double progress = current.EndMs > current.StartMs
                ? Math.Clamp((positionMs - current.StartMs) / (double)(current.EndMs - current.StartMs), 0, 1)
                : 0;

            Bar.SetLyrics(new Components.LyricsView(
                new Components.LyricsLineView(current.Text, current.Syllables, progress, positionMs),
                next == null ? null : new Components.LyricsLineView(next.Text, next.Syllables, 0, positionMs)));

            // ★ 开 60fps 表 —— 关键是**必须把"正在滚动"也算进去** ✗
            // 原因（实测）：IsMarqueeActive / IsKaraokeActive 都是**在渲染里**才置上的 ✓
            // 而这段判断原本每 150ms 才跑一次 → 换句滚动的头 150ms 只有 1~2 帧 ✓
            // 等表开起来滚动都快结束了 ✓✓ 实测"220ms 只画了 3 帧、最大间隔 114ms" ✓
            // 现在：换句那一刻就直接开表 ✓ 并且 tick 的条件也认"正在滚动" ✓ → 全程 60fps ✓
            if (Bar.IsLyricsRolling || Bar.IsMarqueeActive || Bar.IsKaraokeActive || Bar.IsSpectrumActive)
            {
                if (_lyricsTimer is { IsEnabled: false })
                {
                    _lyricsTimer.Start();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("lyrics", "更新歌词显示失败", ex);
        }
    }

    /// <summary>上次记录过信息行数量的曲目（避免每 250ms 重复刷日志）。</summary>
    private Lyrics.LyricTrack? _maskTrack;

    /// <summary>
    /// 计算"制作信息行"的丢弃掩码（行为与参照实现一致，代码自研）：
    /// 命中 0 行、命中全部、或命中数超过总行数一半时**整体放弃过滤** —— 宁可多显示一行，也不清空歌词。
    /// </summary>
    private bool[] ResolveInfoLineMask(Lyrics.LyricTrack track, bool enabled)
    {
        if (!enabled || track.Lines.Count == 0)
        {
            return new bool[track.Lines.Count];
        }

        bool[] flagged = new bool[track.Lines.Count];
        int flaggedCount = 0;
        for (int i = 0; i < track.Lines.Count; i++)
        {
            flagged[i] = IsInfoLine(track.Lines[i].Text);
            if (flagged[i])
            {
                flaggedCount++;
            }
        }

        if (flaggedCount == 0 || flaggedCount == track.Lines.Count || flaggedCount > track.Lines.Count * 0.5)
        {
            return new bool[track.Lines.Count];
        }

        return flagged;
    }

    /// <summary>制作信息行判定（作词/作曲/编曲/制作人等）。</summary>
    private static bool IsInfoLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.Length > 40)
        {
            return false;   // 一整句话不像标注行
        }

        int colon = trimmed.IndexOfAny(new[] { ':', '：' });
        if (colon is <= 0 or > 12)
        {
            return false;   // 冒号位置不对（没有冒号 / 前缀过长）都不算
        }

        string label = trimmed[..colon].Trim();
        foreach (string keyword in InfoLineKeywords)
        {
            if (label.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly string[] InfoLineKeywords =
    {
        "作词", "作曲", "编曲", "词", "曲", "制作人", "监制", "出品", "发行", "录音", "混音", "母带", "和声",
        "吉他", "贝斯", "鼓", "键盘", "弦乐", "钢琴", "合声", "配唱", "统筹", "企划", "企划营销", "母带后期",
        "OP", "SP", "词曲", "原唱", "演唱", "翻唱", "策划", "录音师", "混音师", "母带工程师", "制作",
        "arrange", "arranger", "composer", "lyricist", "producer", "mix", "mixing", "master", "mastered",
        "vocal", "guitar", "bass", "drums", "keyboard", "recorded", "written", "music", "lyrics",
    };

    /// <summary>从当前位置往前找最近一条未被丢弃的行（找不到返回 -1）。</summary>
    private static int CurrentVisibleIndex(Lyrics.LyricTrack track, bool[] dropMask, int positionMs)
    {
        int index = track.IndexAt(positionMs);
        if (index < 0)
        {
            // 落在第一句之前：从后往前找不到就退到第一条可见行
            for (int i = 0; i < track.Lines.Count; i++)
            {
                if (i >= dropMask.Length || !dropMask[i])
                {
                    return i;
                }
            }
            return -1;
        }

        for (int i = index; i >= 0; i--)
        {
            if (i >= dropMask.Length || !dropMask[i])
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>从 index 之后找下一条未被丢弃的行（找不到返回 -1）。</summary>
    private static int NextVisibleIndex(Lyrics.LyricTrack track, bool[] dropMask, int index)
    {
        for (int i = index + 1; i < track.Lines.Count; i++)
        {
            if (i >= dropMask.Length || !dropMask[i])
            {
                return i;
            }
        }
        return -1;
    }

    private void ShowTooltip(string action, string detail, int holdMilliseconds)
        => ShowTooltip(action, detail, holdMilliseconds, null, null);

    /// <summary>显示提示 / 图文通知。<paramref name="artwork"/> 非空即为带封面的切歌通知。</summary>
    private void ShowTooltip(
        string action,
        string detail,
        int holdMilliseconds,
        System.Windows.Media.ImageSource? artwork,
        string? positionOverride)
    {
        try
        {
            TaskbarSample sample = TaskbarSampler.Take();
            if (!sample.Found || _barRect.Width <= 0)
            {
                return;
            }

            Components.TooltipPosition position = positionOverride == null
                ? Components.TooltipWindow.ParsePosition(_settings.Current.Interaction.TooltipPosition)
                : Components.TooltipWindow.ParsePosition(
                    positionOverride,
                    Components.TooltipWindow.ParsePosition(_settings.Current.Interaction.TooltipPosition));

            // 锚点：横向 = 封面左边缘，纵向 = 任务栏上沿（AboveArtwork 用它；其余位置忽略锚点）
            Rect artworkRect = Bar.ArtworkRectDip;
            Rect anchor = new(
                sample.ClientScreenRect.Left + _barRect.Left + (artworkRect.Left * _scale),
                sample.ClientScreenRect.Top,
                0,
                0);

            _tooltip ??= new Components.TooltipWindow();
            _tooltip.Show(action, detail, position, anchor, _scale, holdMilliseconds, artwork);
        }
        catch (Exception ex)
        {
            Log.Write("input", "显示提示失败", ex);
        }
    }

    /// <summary>需要时把宿主显示出来并淡入（已经在显示状态就什么都不做，避免把淡入重启一次）。</summary>
    private void ShowHost(string reason)
    {
        if (!_selfHidden)
        {
            return;
        }
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        _selfHidden = false;
        Log.Write("layout", $"显示宿主: {reason}");
        StartFadeIn();
    }

    /// <summary>把窗口摆到条的客户区位置；offsetY 用于在隐藏瞬间把条"按"在原位做淡出。</summary>
    /// <param name="light">
    /// true = 宽度**动画帧**专用：不传 SWP_FRAMECHANGED ✓
    /// ★ 为什么（这是"滚动时动画掉帧"的真凶）：
    ///   SWP_FRAMECHANGED 会强制重算窗口非客户区、**重建整个分层窗口表面** ✗
    ///   动画每帧都带上它 ⇒ 每帧重建一遍 ⇒ 掉帧 ✗
    ///   而动画帧只改了**宽度**（位置/高度/样式全没变）⇒ 完全不需要它 ✓
    /// </param>
    private void ApplyBarPosition(int offsetY, bool light = false)
    {
        if (_hwnd == IntPtr.Zero || _barRect.Width <= 0 || _barRect.Height <= 0)
        {
            return;
        }

        uint flags = NativeMethods.SWP_NOACTIVATE;
        if (!light)
        {
            flags |= NativeMethods.SWP_FRAMECHANGED;
        }

        _ = NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOP,
            _barRect.Left,
            _barRect.Top + offsetY,
            _barRect.Width,
            _barRect.Height,
            flags);

        if (_barRect.Width != _lastLoggedBarWidth)
        {
            _lastLoggedBarWidth = _barRect.Width;
            Log.Write("layout", $"控件矩形(条内像素): {Bar.DescribeLayout(_scale)}");
        }
    }

    /// <summary>
    /// 隐藏时按需开始淡出。任务栏的矩形是**一步跳**到屏幕外的（父窗口平移到屏外），
    /// 所以要在客户区坐标里反向抵消这个位移，条才会停在原处淡出，而不是瞬间消失。
    /// 返回是否真的开始淡出。
    /// </summary>
    private bool StartFadeOutOnHide(in TaskbarSample sample)
    {
        Configuration.FadeSettings fade = _settings.Current.Fade;
        if (!fade.FadeOutEnabled || fade.FadeOutMilliseconds <= 0 || _selfHidden)
        {
            return false;
        }

        int deltaY = _lastClient.IsEmpty ? 0 : sample.ClientScreenRect.Top - _lastClient.Top;
        _holdOffsetY = -deltaY;
        if (_holdOffsetY != 0)
        {
            ApplyBarPosition(_holdOffsetY);
        }

        // 先取当前（动画中的）透明度，再清动画：清掉动画后属性会回落到基准值（这里是 0），
        // 顺序反了就会读到 0，淡出变成 0 → 0（等于没淡出）。
        double from = Opacity;
        BeginAnimation(OpacityProperty, null);
        Opacity = from;                     // 基准值也跟着设成当前值，避免清动画那一瞬跳到 0

        DoubleAnimation outFade = new()
        {
            From = from,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(fade.FadeOutMilliseconds)),
            FillBehavior = FillBehavior.HoldEnd,
        };
        BeginAnimation(OpacityProperty, outFade);

        _fadeOutDoneAt = Environment.TickCount64 + fade.FadeOutMilliseconds + 60;
        StartFadeTrace("out", fade.FadeOutMilliseconds);
        Log.Write("fade", $"淡出开始 {fade.FadeOutMilliseconds} ms（线性；抵消父窗口位移 {deltaY} px）");
        return true;
    }

    /// <summary>按设置里的延后量开始淡入；未启用淡入就直接显示。</summary>
    private void ScheduleFadeIn()
    {
        Configuration.FadeSettings fade = _settings.Current.Fade;
        if (!fade.FadeInEnabled || fade.FadeInMilliseconds <= 0)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            return;
        }

        if (fade.FadeInDelayMilliseconds <= 0)
        {
            StartFadeIn();
            return;
        }

        DispatcherTimer once = new(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(fade.FadeInDelayMilliseconds),
        };
        once.Tick += (_, _) =>
        {
            once.Stop();
            StartFadeIn();
        };
        once.Start();
    }

    /// <summary>
    /// 淡入宿主。时长与实测的任务栏内容淡入对齐（默认 150 ms、线性）。
    /// 用 WPF 动画而不是在 tick 里逐格改透明度：动画跑在渲染线程上，60 fps 平滑，不受 50 ms 采样限制。
    /// </summary>
    private void StartFadeIn()
    {
        if (_selfHidden)
        {
            return;
        }

        Configuration.FadeSettings fadeSettings = _settings.Current.Fade;
        int milliseconds = Math.Max(1, fadeSettings.FadeInMilliseconds);

        // 先写日志再起动画：日志是同步磁盘写，放在动画开始之后会给第一帧添一次延迟
        Log.Write("fade", $"淡入开始 {milliseconds} ms（线性）");

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;

        DoubleAnimation fade = new()
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            FillBehavior = FillBehavior.HoldEnd,
        };
        BeginAnimation(OpacityProperty, fade);
        StartFadeTrace("in", milliseconds);
    }

    // ---------------------------------------------------------------- 临时：淡入帧追踪
    // RYUBAR_FADE_TRACE=1 时把每一帧的时间与当前透明度写进日志，用来判断
    // "淡入卡顿"到底是渲染帧被饿住，还是动画时钟没按进度走。诊断用，默认关闭。

    private long _fadeTraceStartTicks;
    private int _fadeTraceFrames;
    private EventHandler? _fadeTraceHandler;
    private DispatcherTimer? _fadeTraceStop;

    private void StartFadeTrace(string direction, int milliseconds)
    {
        if (Environment.GetEnvironmentVariable("RYUBAR_FADE_TRACE") != "1")
        {
            return;
        }

        // 上一次淡入/淡出的追踪要先退订，否则短时间内的淡入→淡出会叠加两个追踪器
        StopFadeTrace();

        _fadeTraceStartTicks = Environment.TickCount64;
        _fadeTraceFrames = 0;
        string label = direction;

        _fadeTraceHandler = (_, _) =>
        {
            _fadeTraceFrames++;
            if (_fadeTraceFrames <= 60)
            {
                Log.Write("fadeframe", $"{label} t={Environment.TickCount64 - _fadeTraceStartTicks}ms opacity={Opacity:0.000}");
            }
        };
        CompositionTarget.Rendering += _fadeTraceHandler;

        _fadeTraceStop = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds + 200),
        };
        _fadeTraceStop.Tick += (_, _) =>
        {
            StopFadeTrace();
            Log.Write("fadeframe", $"{label} 完成：{milliseconds} ms 内共 {_fadeTraceFrames} 帧，最终 opacity={Opacity:0.000}");
        };
        _fadeTraceStop.Start();
    }

    private void StopFadeTrace()
    {
        if (_fadeTraceHandler != null)
        {
            CompositionTarget.Rendering -= _fadeTraceHandler;
            _fadeTraceHandler = null;
        }
        if (_fadeTraceStop != null)
        {
            _fadeTraceStop.Stop();
            _fadeTraceStop = null;
        }
    }

    /// <summary>取消淡入并把透明度归零：条离屏期间必须是完全透明的。</summary>
    private void CancelFade()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
    }

    /// <summary>
    /// 把自己抬到同级窗口最上面。任务栏的内容由 XAML 内容桥（`DesktopWindowContentBridge`）合成，
    /// 它是我们的兄弟窗口；Shell 在隐藏/露出过程中可能重排同级窗口，一旦我们掉到桥下面，
    /// 条就会被桥的绘制盖住 —— 表现出来就是"淡入中途卡一下"。
    /// 所以露出时、落位后、以及可见期间定期都要抬一次（很便宜：不改尺寸不改位置）。
    /// </summary>
    private void RaiseAboveSiblings()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS);
    }

    private void SetTickMilliseconds(int milliseconds)
    {
        if (Math.Abs(_timer.Interval.TotalMilliseconds - milliseconds) > 0.5)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        }
    }

    // ---------------------------------------------------------------- 消息

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 托盘图标回调（右键菜单 / 双击打开设置）
        if (msg == Interop.TrayIcon.CallbackMessage)
        {
            handled = true;
            _tray?.HandleMessage((int)lParam, OpenSettingsFromTray, ExitFromTray);
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_NCDESTROY)
        {
            // 窗口正在销毁：不要在这里做重活，交给持有方在下一个 Dispatcher 帧重建
            RaiseInvalidated("wm-ncdestroy");
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_GETOBJECT)
        {
            // 不要把宿主暴露给 UIA：否则会被自己算成"已占用"
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        if (_taskbarCreatedMessage != 0 && unchecked((uint)msg) == _taskbarCreatedMessage)
        {
            handled = true;
            Log.Write("host", "收到 TaskbarCreated：Explorer 重启，准备重新挂载");
            _attached = false;
            _taskbar = IntPtr.Zero;
            _motion.Reset();
            _frozen = true;
            _probeDue = true;
            _taskbarHidden = false;
            HideHost("explorer-restart");
            // explorer 重启会把托盘图标一起清掉，必须重新添加
            _tray?.Add("RyuBar");
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- 诊断

    /// <summary>
    /// 当前媒体快照（合并后的：SMTC + 需要时补的内存位置 / 本地 JSON 时长）。
    /// 写进 state.json 是为了让诊断工具看到"界面真正显示的那个值"，而不是 SMTC 的原始值。
    /// </summary>
    private object BuildMediaState()
    {
        Media.MediaSnapshot snapshot = _media.Snapshot;
        DateTimeOffset now = DateTimeOffset.Now;
        return new
        {
            source = snapshot.Source,
            title = snapshot.Title,
            artist = snapshot.Artist,
            hasSession = snapshot.HasSession,
            hasTimeline = snapshot.HasTimeline,
            isPlaying = snapshot.IsPlaying,
            canSeek = snapshot.CanSeek,
            positionSeconds = Math.Round(snapshot.Position.TotalSeconds, 2),
            displaySeconds = Math.Round(snapshot.PositionAt(now).TotalSeconds, 2),
            durationSeconds = Math.Round(snapshot.Duration.TotalSeconds, 2),
            barWidth = Math.Round(Bar.BarWidth, 1),
        };
    }

    /// <summary>任务栏上最大的连续空闲区（DIP）。设置窗口用它来判断"固定宽度是否会放不下"。</summary>
    private double MaxFreeGapDip(in TaskbarSample sample)
    {
        if (!sample.Found || _occupied.Count == 0)
        {
            return 0;
        }

        int cursor = 0;
        int best = 0;
        foreach (OccupiedRange range in _occupied.OrderBy(r => r.Left))
        {
            int left = range.Left - sample.ClientScreenRect.Left;
            int right = range.Right - sample.ClientScreenRect.Left;
            if (left > cursor)
            {
                best = Math.Max(best, left - cursor);
            }
            cursor = Math.Max(cursor, right);
        }
        if (cursor < sample.ClientWidth)
        {
            best = Math.Max(best, sample.ClientWidth - cursor);
        }

        double scale = _scale <= 0 ? 1.0 : _scale;
        return Math.Round(best / scale);
    }

    private void WriteState(in TaskbarSample sample)
    {
        try
        {
            object state = new
            {
                time = DateTime.Now.ToString("O"),
                attached = _attached,
                taskbar = $"0x{_taskbar.ToInt64():X}",
                clientWidth = sample.Found ? sample.ClientWidth : 0,
                clientHeight = sample.Found ? sample.ClientHeight : 0,
                scale = _scale,
                hidden = _selfHidden || _taskbarHidden,
                hostHidden = _selfHidden,
                taskbarHidden = _taskbarHidden,
                opacity = Math.Round(Opacity, 3),
                frozen = _frozen,
                reason = _reason,
                bar = new { left = _barRect.Left, top = _barRect.Top, width = _barRect.Width, height = _barRect.Height, maxFreeDip = MaxFreeGapDip(sample), minWidthDip = Bar.MinimumTotalWidthDip, maxTextDip = _maxTextWidthDip, liveTextDip = _liveTextWidthDip, usableDip = _usableDip, chromeDip = _chromeDip, minChromeDip = _minChromeDip },
                occupied = _occupied.Select(r => new { r.Left, r.Right, r.Name }).ToArray(),
                probeSource = _probeSource,
                probeDetail = _probeDetail,
                media = BuildMediaState(),
            };

            string path = System.IO.Path.Combine(Log.Directory, "..", "state.json");
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

            // 磁盘写放到线程池：状态文件是给人和诊断工具看的，不值得占用 UI 线程
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    File.WriteAllText(path, json);
                }
                catch
                {
                }
            });
        }
        catch (Exception ex)
        {
            Log.Write("state", "写状态文件失败", ex);
        }
    }
}
