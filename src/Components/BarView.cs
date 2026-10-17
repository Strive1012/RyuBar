using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RyuBar.Lyrics;

namespace RyuBar.Components;

/// <summary>条上可以被点到的区域。</summary>
public enum BarHit
{
    None,
    Artwork,
    Text,
    Previous,
    PlayPause,
    Next,
    Progress,
}

/// <summary>一行歌词的显示数据（宿主按播放位置算好，视图只负责画）。</summary>
public sealed record LyricsLineView(
    string Text,
    IReadOnlyList<LyricSyllable>? Syllables,
    double Progress,
    int PositionMs);

/// <summary>歌词显示数据：当前行 + 下一句（下一句可为空）。</summary>
public sealed record LyricsView(LyricsLineView? Current, LyricsLineView? Next);

/// <summary>
/// 媒体条的内容：封面 + 标题/歌手 + 上一首/播放暂停/下一首 + 进度条 + 时间。
/// 有歌词时标题/歌手那两行换成歌词（当前行 + 下一句），没有歌词就回落到标题/歌手。
///
/// 自绘（而不是用 XAML 拼布局）的两个理由：
///  1. 阶段 3 的外观对齐要在同一个坐标系里调圆角、描边、顶部高光、悬停调色板，自绘最直接；
///  2. 宽度要按内容自适应（标题长短不同），自绘能精确算出来，不用和 WPF 的布局系统来回猜。
///
/// 单位一律是 DIP；窗口的物理尺寸由宿主按 DPI 换算。
/// </summary>
public sealed class BarView : FrameworkElement
{
    // ---- 度量（阶段 3 会把它们做成可配置）----
    private const double PaddingX = 8;
    private const double ArtSize = 26;
    private const double ArtRadius = 5;
    private const double ArtTextGap = 8;
    private const double ButtonSize = 24;
    private const double ButtonRadius = 4;
    private const double ButtonGap = 2;
    private const double ProgressWidth = 70;
    private const double ProgressHeight = 3;
    private const double ProgressGap = 10;
    private const double TimeGap = 6;
    private const double TitleSize = 13;
    private const double ArtistSize = 11;
    private const double TimeSize = 11;
    /// <summary>
    /// 媒体条构成开关：环形进度 / 条状进度 / 控制按钮 / 环上可拖动跳转。
    /// 这几样都关也能用（只剩封面 + 标题歌词 + 时间）—— 宽度随之变化，所以必须重新量。
    /// </summary>
    public void ApplyBarElements(bool ringProgress, bool barProgress, bool showControls, bool ringSeek, string ringColor, int ringOpacity, int ringThicknessDip)
    {
        string color = string.IsNullOrWhiteSpace(ringColor) ? "accent" : ringColor;
        int opacity = Math.Clamp(ringOpacity, 0, 100);
        int thickness = Math.Clamp(ringThicknessDip, 1, 3);

        // ★ 值全都没变就直接返回 ✗
        //   原来每被调用一次都会做一遍：重新 Measure + InvalidateMeasure + InvalidateVisual + 写一行日志 ✓
        //   而主程序每 150ms 调它一次 ⇒ 日志被刷（实测每秒 6~7 行）+ 每 150ms 一次无意义的布局 ✗
        if (ringProgress == _ringProgress && barProgress == _barProgress && showControls == _showControls
            && ringSeek == _ringSeek && color == _ringColor && opacity == _ringOpacity && thickness == _ringThicknessDip)
        {
            return;
        }

        _ringProgress = ringProgress;
        _barProgress = barProgress;
        _showControls = showControls;
        _ringSeek = ringSeek;
        _ringColor = color;
        _ringOpacity = opacity;
        _ringThicknessDip = thickness;
        _ringAccent = null;   // 颜色/透明度可能变了 → 丢掉缓存
        Diagnostics.Log.Write("ring", $"应用：开关环={ringProgress} 颜色={_ringColor} 不透明度={_ringOpacity}% 粗细={_ringThicknessDip}DIP 条状={barProgress} 按钮={showControls}");
        Measure(Size.Empty);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>环形进度的已播颜色：`accent` = 跟随系统强调色；否则是 `#RRGGBB`/`#AARRGGBB`。</summary>
    private string _ringColor = "accent";

    /// <summary>环形进度已播部分的不透明度（0~100 %）。</summary>
    private int _ringOpacity = 100;

    /// <summary>环形进度线条粗细（DIP）：已播线用它，轨道用它的 55%。</summary>
    private double _ringThicknessDip = 3;

    /// <summary>本次布局的整体缩放系数（= 条高 / 36 的设计基准，夹在 0.75~1.45）。</summary>
    private double _contentScale = 1;

    /// <summary>歌词/文字区字号倍率（用户设置，1.0 = 标准）。</summary>
    private double _fontScale = 1.0;

    /// <summary>换句滚动过渡的起始时刻（default = 不滚动）。</summary>
    private DateTime _lyricsRollAt;

    /// <summary>歌词自然宽度缓存（每帧重测会掉帧 ✗ 见 ComputeLayout 里的说明）。</summary>
    private double _lyricsNeedWidth;

    private string _lyricsNeedKey = "";

    /// <summary>上一句的文本：用来判断"两句都非空"才允许滚动（避免没歌词时反复往上跳 ✓）。</summary>
    private string _lyricsPreviousText = "";

    /// <summary>标题/歌手/时间的排版缓存 + 缓存键（每帧重建会掉帧 ✗ 见 ComputeLayout 说明）。</summary>
    private FormattedText? _cachedTitleText;

    private FormattedText? _cachedArtistText;
    private FormattedText? _cachedTimeText;
    private string _textCacheKey = "";

    /// <summary>换句滚动时长（ms）：与宽度缓动同量级 ✓ 两者一起发生时观感一致 ✓</summary>
    private const double LyricRollDurationMs = 220;

    /// <summary>
    /// 当前是否正在播放"换句滚动"过渡。
    /// 宿主据此**跳过窗口宽度缓动** ✓✓ —— 滚动期间每帧改窗口宽度（分层子窗口要重建表面 ✓）会明显掉帧
    ///（用户实测"滚动动画还是卡" ✓ 而 trace 显示视图渲染只用 1.1ms ✓ → 卡的根源就是窗口宽度动画 ✓）。
    /// 两者串行：滚动时宽度直接跳 ✓ 不滚动时才缓动 ✓
    /// </summary>
    public bool IsLyricsRolling => _lyricsRollAt != default
        && (DateTime.Now - _lyricsRollAt).TotalMilliseconds < LyricRollDurationMs;

    // ---------------------------------------------------------------- 频谱（先做样子 ✓）

    /// <summary>频谱开关 + 柱数（由设置传入 ✓）。</summary>
    public void ApplySpectrum(bool enabled, int bars)
    {
        int clampedBars = Math.Clamp(bars, 4, 16);
        // ★ 值没变就直接返回 ✗ 
        // ApplySpectrum 会被"每帧的重新应用设置"路径调用 ✓ 之前每次都重测量 + 写日志 ✗
        // → 日志被刷屏几百条 ✓ 而且每帧一次完整重测量本身就会掉帧 ✓（实测抓到 ✓）
        if (_spectrum == enabled && _spectrumBars == clampedBars)
        {
            return;
        }

        _spectrum = enabled;
        _spectrumBars = clampedBars;
        Diagnostics.Log.Write("spectrum", $"应用：开关={_spectrum} 柱数={_spectrumBars}");
        Measure(Size.Empty);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private bool _spectrum;
    private int _spectrumBars = 9;

    /// <summary>频谱柱宽 / 柱间距 / 整块左右留白（DIP，会随整体缩放一起放大 ✓）。</summary>
    private const double SpectrumBarWidth = 2.4;
    private const double SpectrumBarGap = 2.0;
    private const double SpectrumOuterGap = 10.0;

    /// <summary>频谱需要连续重绘（和逐字/跑马灯一样 ✓），宿主据此保持 60fps 表。</summary>
    public bool IsSpectrumActive => _spectrum && _snapshot.HasSession;

    /// <summary>频谱块的总宽（DIP，含两侧留白）；关掉时返回 0 ✓。</summary>
    private double SpectrumWidth =>
        _spectrum ? SpectrumOuterGap + (_spectrumBars * ((SpectrumBarWidth + SpectrumBarGap) * _contentScale)) : 0;

    /// <summary>
    /// 接收**真实频谱数据**（16 段 0~1 ✓ 由宿主从 WASAPI 环回 + FFT 分析器取来 ✓）。
    /// hasRealData=false 时画"低平柱" ✓ —— **不假装有数据** ✓（采集失败要看得出来 ✓）。
    /// </summary>
    public void SetSpectrumLevels(float[] levels, bool hasRealData)
    {
        int n = Math.Min(levels.Length, _spectrumLevels.Length);
        for (int i = 0; i < n; i++)
        {
            _spectrumLevels[i] = levels[i];
        }
        _hasRealSpectrum = hasRealData;
    }

    private readonly float[] _spectrumLevels = new float[16];
    private bool _hasRealSpectrum;

    /// <summary>
    /// 画频谱柱。
    /// 数据来源：宿主把 **WASAPI 环回 + 自写 FFT** 分析出的 16 段能量传进来 ✓（见 SpectrumAnalyzer ✓）
    /// 本方法只负责"16 段 → N 根柱"的映射与绘制 ✓ 柱数改了不用碰采集侧 ✓。
    /// </summary>
    private void DrawSpectrum(DrawingContext dc, double x, double cy, double height)
    {
        if (!_spectrum || _spectrumBars <= 0)
        {
            return;
        }

        double barW = SpectrumBarWidth * _contentScale;
        double gap = SpectrumBarGap * _contentScale;
        double maxH = Math.Max(4, height * 0.72);
        double minH = Math.Max(2, height * 0.10);

        // 没有真数据时：柱子贴底（一眼能看出"没接上" ✓ 而不是给它编一个波形 ✗）
        double t = DateTime.Now.TimeOfDay.TotalSeconds;
        Brush brush = AccentBrush;
        for (int i = 0; i < _spectrumBars; i++)
        {
            double level;
            if (_hasRealSpectrum)
            {
                // 16 段 → N 根：按比例取一段区间求平均 ✓（柱数任意都不浪费数据 ✓）
                int from = (int)Math.Floor(i * _spectrumLevels.Length / (double)_spectrumBars);
                int to = Math.Max(from + 1, (int)Math.Ceiling((i + 1) * _spectrumLevels.Length / (double)_spectrumBars));
                float sum = 0;
                int count = 0;
                for (int b = from; b < to && b < _spectrumLevels.Length; b++)
                {
                    sum += _spectrumLevels[b];
                    count++;
                }
                level = count == 0 ? 0 : sum / count;
            }
            else
            {
                level = 0.03 + (0.02 * Math.Sin(t + i));   // 极轻微的呼吸 ✓ 表示"在跑但没数据" ✓
            }

            double h = minH + ((maxH - minH) * Math.Clamp(level, 0, 1));
            double bx = x + (i * (barW + gap));
            double by = cy - (h / 2);
            dc.DrawRoundedRectangle(brush, null, new Rect(bx, by, barW, h), barW / 2, barW / 2);
        }
    }

    // ---- 滚动期间的逐帧计时（一秒一条的统计采不到 220ms 的滚动 ✗ 所以单独量 ✓）----
    private long _rollLastTick;
    private int _rollFrames;
    private double _rollTotalMs;
    private double _rollMaxGapMs;

    /// <summary>在每帧渲染末尾调用：记录滚动期间的帧间隔（最大间隔 = 那一次卡顿 ✓）。</summary>
    private void TraceRollFrame()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!IsLyricsRolling)
        {
            if (_rollLastTick != 0)
            {
                Diagnostics.Log.Write("roll", $"滚动结束：共 {_rollFrames} 帧，平均间隔 {(_rollFrames == 0 ? 0 : _rollTotalMs / _rollFrames):0.0}ms，最大间隔 {_rollMaxGapMs:0.0}ms");
                _rollLastTick = 0;
                _rollFrames = 0;
                _rollTotalMs = 0;
                _rollMaxGapMs = 0;
            }
            return;
        }

        if (_rollLastTick != 0)
        {
            double gapMs = (now - _rollLastTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _rollFrames++;
            _rollTotalMs += gapMs;
            _rollMaxGapMs = Math.Max(_rollMaxGapMs, gapMs);
        }
        _rollLastTick = now;
    }

    /// <summary>封面大小倍率（用户设置，1.0 = 与两行歌词等高）。**与字号独立** ✓</summary>
    private double _artScale = 1.0;

    private double _buttonSize = ButtonSize;
    private double _buttonGap = ButtonGap;
    private double _progressHeight = ProgressHeight;
    private double _titleSize = TitleSize;
    private double _artistSize = ArtistSize;
    private double _timeSize = TimeSize;

    /// <summary>轨道线宽 = 已播线宽 × 0.55（保持"已播更突出"的层次）。</summary>
    private double TrackThicknessDip => Math.Max(0.8, _ringThicknessDip * 0.55);

    private bool _ringProgress;
    private bool _barProgress = true;
    private bool _showControls = true;
    private bool _ringSeek = true;

    private const double RingOutsetDip = 0.0;

    /// <summary>轨道线宽（未播部分）：细一点，让已播部分更突出。</summary>
    private const double RingThicknessDip = 1.6;

    /// <summary>已播线宽：**比轨道粗**，这样用户改的颜色才是视觉主角（用户反馈"改颜色没用"的根因）。</summary>
    private const double RingAccentThicknessDip = 3.0;

    /// <summary>
    /// 环的路径矩形 = 封面矩形**内缩半个笔宽** ✗ 不外扩 ✓：
    /// 描边是以路径为中心画的 ✓ 内缩半个笔宽后，描边正好**压在封面边界上** ✓
    /// → 观感就是"**这张图片自己的外边框**"（用户明确要求 ✓ 不要缝隙 ✓）。
    /// </summary>
    private Rect RingRect(Rect art)
    {
        double inset = ((_ringThicknessDip * _contentScale) / 2) + RingOutsetDip;
        return new Rect(
            art.X + inset,
            art.Y + inset,
            Math.Max(0, art.Width - (inset * 2)),
            Math.Max(0, art.Height - (inset * 2)));
    }

    /// <summary>环的圆角半径（与封面同心同形状）。</summary>
    private static double RingRadius(Rect ring)
    {
        return Math.Max(0.5, Math.Min(ArtRadius, Math.Min(ring.Width, ring.Height) / 2));
    }

    /// <summary>
    /// 空闲态：**连媒体会话都没有**（没有音源 ✓）。
    /// 用户要求这种状态下"封面用音符图标占位，且不显示歌词 / 进度条 / 上下首与播放键" ✓
    /// ⇒ 条子收成只有封面大小的一小块 ✓
    /// </summary>
    private bool IsIdle => !_snapshot.HasSession;

    /// <summary>条状进度是否真的画：要同时满足"有音源 + 曲目有时间轴 + 用户开了条状"。</summary>
    private bool ShowBarProgress => !IsIdle && _snapshot.CanShowProgress && _barProgress;

    /// <summary>时间是否画：与条状进度**绑定**（用户要求：一起出现、一起消失）。</summary>
    private bool ShowTimeText => !IsIdle && _snapshot.CanShowProgress && _barProgress;

    /// <summary>环形进度是否真的画：要同时满足"有音源 + 曲目有时间轴 + 用户开了环形"。</summary>
    private bool ShowRingProgress => !IsIdle && _snapshot.CanShowProgress && _ringProgress;

    /// <summary>每帧现算的播放进度（0~1）；拖动中优先用拖动值。</summary>
    private double CurrentRatio()
    {
        return _draggingProgressRatio >= 0 ? _draggingProgressRatio : _snapshot.ProgressAt(DateTimeOffset.Now);
    }

    /// <summary>环形进度的几何：半径 = 封面半径 + 外扩，圆心 = 封面中心。</summary>
    private (Point Center, double Radius) RingGeometry(Rect art)
    {
        return (new Point(art.X + (art.Width / 2), art.Y + (art.Height / 2)), (art.Width / 2) + RingOutsetDip);
    }

    private const double TextMaxWidth = 170;
    private const double TextMinWidth = 40;

    /// <summary>
    /// 固定宽度模式下的文字区最小宽度（≈2 个汉字）。文字区太小没关系 —— 超出的长句靠跑马灯滚动展示。
    /// 它是"整条可调下限"的一部分：下限 = 其余部件总宽 + 本值。
    /// </summary>
    public const int FixedTextMinWidthDip = 24;

    // 以下 6 个字段 = 本次布局实际使用的尺寸（**排版 / 绘制 / 命中测试三方共用**，
    // 否则"按 26 画、按 22 排"就会内容错位）。
    // 现在只有一套尺寸 = 下面的常量；把尺寸收进字段是为了以后加"紧凑皮肤"时不必改三处。
    private double _padX = PaddingX;
    private double _artSize = ArtSize;
    private double _artTextGap = ArtTextGap;
    private double _progressWidth = ProgressWidth;
    private double _progressGap = ProgressGap;
    private double _timeGap = TimeGap;

    private static readonly Brush ChipBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x1A, 0x1E, 0x28)));
    private static readonly Pen ChipBorder = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))), 1));

    /// <summary>本次生效的面板画刷（随「背景不透明度」变化 ✓ 所以用实例字段而不是常量 ✓）。</summary>
    private Brush _chipBackground = ChipBackground;

    private Pen _chipBorder = ChipBorder;

    /// <summary>已经生效的面板不透明度（-1 = 还没设过 ✓ 用于早退 ✓）。</summary>
    private int _appliedBackgroundOpacity = -1;

    /// <summary>
    /// 媒体条**背景面板**的不透明度（0~100 %）。只动面板 ✓ 封面/文字/按钮不变 ✓
    /// （用户要求"让 bar 可以变得透明" ✓ 参照 AFM 观感 ✓：面板半透明、内容实心 ✓）
    /// </summary>
    public void ApplyBarAppearance(int backgroundOpacity)
    {
        int pct = Math.Clamp(backgroundOpacity, 0, 100);

        // ★ 值没变就什么都不做 ✗
        //   原来这里每被调用一次都会：新建 2 个画刷 + 1 个 Pen、写一行日志、再强制重绘 ✗
        //   而主程序每 150ms 调它一次 ⇒ 白耗内存 + 日志被刷（实测每秒 6~7 行「背景不透明度」）✗
        if (pct == _appliedBackgroundOpacity)
        {
            return;
        }

        _appliedBackgroundOpacity = pct;
        byte fillAlpha = (byte)Math.Round(0xE6 * pct / 100.0);
        byte borderAlpha = (byte)Math.Round(0x33 * pct / 100.0);

        _chipBackground = Freeze(new SolidColorBrush(Color.FromArgb(fillAlpha, 0x1A, 0x1E, 0x28)));
        _chipBorder = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(borderAlpha, 0xFF, 0xFF, 0xFF))), 1));
        Diagnostics.Log.Write("bar", $"背景不透明度 = {pct}%（面板 alpha={fillAlpha:X2} 边框 alpha={borderAlpha:X2}）");
        InvalidateVisual();
    }
    private static readonly Brush TitleBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ArtistBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TimeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GlyphBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xEA, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GlyphDisabledBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)));

    /// <summary>播放器不支持跳转时的进度条颜色：灰一些，一眼能看出"这条只能看，点不动"。</summary>
    private static readonly Brush AccentDisabledBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x7C, 0x83, 0x8C)));
    private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush PressedBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ArtPlaceholderBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));

    /// <summary>
    /// 没有封面时的**音符**颜色（蓝灰 ✓ 与媒体条的低饱和蓝底协调 ✓）。
    /// 用户："像这种只显示图标，不填充其他部分"✓
    /// 原来用 GlyphDisabledBrush（很淡 ✓）＋ 一块浅白圆角底 ⇒ 看着像"一个空封面"✗
    /// 现在只画音符 ✓ 颜色稍提亮到能看清又不抢眼 ✓
    /// </summary>
    private static readonly Brush NotePlaceholderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x93, 0xA3, 0xBC)));

    /// <summary>封面悬停时叠在图上的一层浅亮（透明度靠 PushOpacity 控制 ✓ 不用每帧新建画刷 ✗）。</summary>
    private static readonly Brush ArtHoverTint = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)));

    /// <summary>
    /// 封面中心播放/暂停图标的圆底。
    /// ★ 用**淡黑**+白图标 ✗ 不用白底黑图标 ✓
    ///   （白底太抢眼，像在封面上贴了块白膏药 ✗ —— 用户明确反馈"按钮太丑"✓）
    ///   淡黑半透明圆 + 白图标是最克制的做法 ✓ 和右侧控制按钮的观感也统一 ✓
    /// </summary>
    private static readonly Brush ArtGlyphBackdrop = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)));

    /// <summary>封面中心图标颜色（白色 ✓ 配淡黑圆底 ✓）。</summary>
    private static readonly Brush ArtGlyphBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));

    private Media.MediaSnapshot _snapshot = Media.MediaSnapshot.Empty;
    private BarHit _hover = BarHit.None;
    private BarHit _pressed = BarHit.None;
    private double _draggingProgressRatio = -1;

    // ---- 封面悬停动画（用户："光标选中后封面微微放大，高亮效果，且出现暂停或播放按钮"✓
    //                      "变色过程需要动画"✓）----

    /// <summary>封面悬停的缓动进度：0 = 常态，1 = 完全悬停（放大 / 高亮 / 图标全显出 ✓）。</summary>
    private double _artHoverT;

    /// <summary>悬停动画的目标值（0 或 1 ✓）。</summary>
    private double _artHoverTarget;

    private double _artHoverFrom;
    private DateTime _artHoverStart;

    /// <summary>过渡时长（用户要求"变色过程需要动画"✓ 140ms 比 220ms 更跟手 ✓）。</summary>
    private const int ArtHoverDurationMs = 140;

    /// <summary>悬停过渡的驱动定时器（只在过渡期间跑 ✓ 到位就停 ✗ 不常驻占用 ✓）。</summary>
    private System.Windows.Threading.DispatcherTimer? _artHoverTimer;

    // ---- 歌词 ----
    private LyricsView? _lyrics;
    private int _lyricsLines = 2;
    private bool _lyricsPerWord = true;
    private bool _lyricsMarquee = true;
    private string _marqueeText = "";
    private DateTime _marqueeAnchor = DateTime.Now;

    /// <summary>诊断：最近一次渲染时文字区可用宽度与歌词缓存宽度。</summary>
    public double LastAvailableDip { get; private set; }

    public double LastCachedWidth { get; private set; }

    /// <summary>诊断：歌词与跑马灯的内部状态（用来确认滚动分支为什么没触发）。</summary>
    public string DescribeLyricsState()
    {
        LyricsView? lyrics = _lyrics;
        return $"歌词={(lyrics?.Current == null ? "无" : $"「{lyrics.Current.Text}」")}"
            + $" 行数设置={_lyricsLines} 逐字={_lyricsPerWord} 跑马灯={_lyricsMarquee} 强制窄={_forceMarquee}"
            + $" 可用宽={LastAvailableDip:0.0} 缓存宽={LastCachedWidth:0.0} 有逐字位置={_cacheCurrent.HasPieces}"
            + $" 判定应滚动={(_lyricsMarquee && LastCachedWidth > LastAvailableDip - 4)} 滚动中={IsMarqueeActive}"
            + $" 条宽DIP={ActualWidth:0.0}";
    }

    /// <summary>诊断开关：把歌词文字区压窄以强制触发跑马灯（只用于性能验证）。</summary>
    private readonly bool _forceMarquee = Environment.GetEnvironmentVariable("RYUBAR_FORCE_MARQUEE") == "1";

    /// <summary>当前是否有可显示的歌词（决定文字区宽度与是否画歌词）。</summary>
    private bool HasLyricsText => _lyrics?.Current is { Text.Length: > 0 };

    /// <summary>跑马灯速度（DIP/秒）。参照实现用 40。</summary>
    private const double MarqueeSpeedDipPerSecond = 40;

    /// <summary>跑马灯引入延迟：新行先静止一会儿再滚，避免每句都在动。</summary>
    private const double MarqueeIntroSeconds = 1.0;

    /// <summary>当前是否有长行正在滚动（宿主据此决定要不要按 60fps 连续重画）。</summary>
    public bool IsMarqueeActive { get; private set; }

    /// <summary>当前是否在做逐字推进（同样需要 60fps 连续重画，否则高亮会按进度定时器的节奏跳）。</summary>
    public bool IsKaraokeActive { get; private set; }

    /// <summary>设置歌词显示数据（null = 没有歌词，回落到标题/歌手）。</summary>
    public void SetLyrics(LyricsView? view)
    {
        string text = view?.Current?.Text ?? "";
        bool lineChanged = !string.Equals(text, _lyrics?.Current?.Text, StringComparison.Ordinal);
        _lyrics = view;
        // ★ 只有"旧句与新句都真的有内容"时才启动滚动 ✓
        // 否则"空 ↔ 占位内容"来回切换会被当成换句 ✗ → 没有歌词时动画反复往上跳（用户实测 ✓）
        bool bothHaveText = !string.IsNullOrWhiteSpace(text)
            && !string.IsNullOrWhiteSpace(_lyricsPreviousText);
        _lyricsPreviousText = text;
        if (lineChanged)
        {
            _marqueeAnchor = DateTime.Now;   // 换行：跑马灯从头开始
            if (bothHaveText)
            {
                _lyricsRollAt = DateTime.Now;   // 换行：启动"向上滚动一行"的过渡（用户要求 ✓）
            }
            Measure(Size.Empty);
            InvalidateMeasure();
        }
        InvalidateVisual();
    }

    /// <summary>歌词相关设置（行数 / 逐字 / 跑马灯 / 宽度与自动适配 / 配色 / 对齐 / 字号 / 封面）。</summary>
    public void ApplyLyricsSettings(
        int lines,
        bool perWord,
        bool marquee,
        int widthDip,
        string textColor,
        string highlightColor,
        string nextLineColor,
        int alignment,
        int fontScalePercent,
        int artSizePercent,
        bool autoFitWidth)
    {
        _lyricsLines = Math.Clamp(lines, 1, 2);
        _lyricsPerWord = perWord;
        _lyricsMarquee = marquee;
        _lyricsTextColor = textColor;
        _lyricsHighlightColor = highlightColor;
        _lyricsNextLineColor = nextLineColor;
        _lyricsAlignment = Math.Clamp(alignment, 0, 2);   // 0=左 1=居中 2=右
        _fontScale = Math.Clamp(fontScalePercent, 80, 150) / 100.0;
        _artScale = Math.Clamp(artSizePercent, 60, 160) / 100.0;
        int clampedWidth = Math.Clamp(widthDip, 100, 900);   // 与 LyricsSettings.Normalize 保持一致（上限 900 ✓）
        // ★ 自动适配开关变化**也必须重量** ✗ 只比宽度会漏掉开关
        //   （关掉自动适配时宽度值没变 → 不触发重测 → 视图还按旧的自动宽度画 → 用户看到"开关点了没反应" ✗）
        if (clampedWidth != _lyricsWidthDip || autoFitWidth != _lyricsAutoFit)
        {
            _lyricsWidthDip = clampedWidth;
            _lyricsAutoFit = autoFitWidth;
            Measure(Size.Empty);       // 宽度变了要重新量，窗口才会跟着变宽
            InvalidateMeasure();
        }
        InvalidateVisual();
    }

    /// <summary>
    /// 「安全区」固定整条宽度（DIP，0 = 按内容自适应）。
    /// 富余或不足的宽度都吸收进文字/歌词区，其它部件尺寸不变。
    /// </summary>
    public void SetForcedWidthDip(int dip)
    {
        int clamped = dip <= 0 ? 0 : Math.Clamp(dip, 120, 900);
        if (clamped == _forcedWidthDip)
        {
            return;
        }

        _forcedWidthDip = clamped;
        Measure(Size.Empty);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// 自动适配模式的**宽度上限**（DIP）= 任务栏空档能给文字区的最大宽度，由宿主每轮落位时写入（≤0 清除）。
    /// 为什么需要它：用户选定的方案里，自动模式**不给用户配上限** ✗
    /// 但长句必须有天花板，否则媒体条会被撑到占满整个任务栏 ✗（空档够不够由宿主实测，见 Place → MaxFreeGapDip）。
    /// 空档与文字区宽度**无关**（只跟任务栏图标占用有关）→ 本值稳定 → 不会和宽度缓动互相追着跑 ✓
    /// </summary>
    public void SetAutoFitWidthCapDip(int dip)
    {
        int clamped = dip <= 0 ? 0 : Math.Clamp(dip, 120, 900);
        if (clamped == _autoFitCapDip)
        {
            return;
        }

        _autoFitCapDip = clamped;
        Measure(Size.Empty);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>文字区（歌词/标题）的最小宽度 —— 和设置界面「文字区宽度」的可调下限保持一致。</summary>
    public const int MinimumTextWidthDip = 120;

    /// <summary>整条的可行最小宽度（DIP，整数、整十）= 其余部件总宽 + 文字区最小宽度。设置界面用它当「固定宽度」的可调下限。</summary>
    public int MinimumTotalWidthDip { get; private set; } = 300;

    /// <summary>
    /// 除文字区以外的部件总宽（DIP）= 左右留白 + 封面 + 封面间距 + 按钮 + 频谱 + 进度条 + 时间 + 间隙。
    /// 宿主用它反推"**这套配置下**文字区最多能占多少" ✓
    /// </summary>
    public int ChromeWidthDip { get; private set; }

    /// <summary>
    /// 「必需部件」宽度（DIP）= 留白 + 封面 + 间距 + 时间（不含可开关的频谱/按钮/进度条）。
    /// 宿主用它算**最大限位**（= 这台机器真能达到的最大文字区宽度）✓ 所以限位不随开关部件浮动 ✓
    /// </summary>
    public int MinChromeWidthDip { get; private set; }

    private int _forcedWidthDip;

    private int _lyricsWidthDip = 280;

    /// <summary>歌词宽度自动适配（默认开）。关 = 用 _lyricsWidthDip 当**固定**宽度。</summary>
    private bool _lyricsAutoFit = true;

    /// <summary>自动适配模式的宽度上限（DIP，≤0 = 还没测到空档）。由宿主按任务栏空档写入，见 SetAutoFitWidthCapDip。</summary>
    private int _autoFitCapDip;

    /// <summary>歌词对齐：0=左对齐 1=居中 2=右对齐（跑马灯滚动时无效，滚动总是从左边开始）。</summary>
    private int _lyricsAlignment;
    private string _lyricsTextColor = "#FFFFFF";
    private string _lyricsHighlightColor = "#4CC2FF";
    private string _lyricsNextLineColor = "#8AFFFFFF";

    /// <summary>把 #RRGGBB / #AARRGGBB 解析成画刷；解析失败退回给定画刷。</summary>
    private static Brush BrushFromColor(string? value, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string text = value.Trim().TrimStart('#');
        if (text.Length is not (6 or 8))
        {
            return fallback;
        }

        try
        {
            uint raw = System.Convert.ToUInt32(text, 16);
            Color color = text.Length == 8
                ? Color.FromArgb((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw)
                : Color.FromRgb((byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
            return Freeze(new SolidColorBrush(color));
        }
        catch
        {
            return fallback;
        }
    }

    public BarView()
    {
        // 不吸附整像素：进度条的填充宽度是**亚像素**变化的（105 px 的条上，4 分钟的歌一秒只走 0.42 px）。
        // 一旦吸附到整像素，就会变成"每 2~3 秒才跳一格"，看起来像延迟 —— 实测参照实现的线横跨约 550 px
        // （2.4 px/秒），所以它每秒都看得出在动。关掉吸附后填充会以抗锯齿的方式连续推进，观感一致、外观不变。
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
        ClipToBounds = false;
        MouseLeave += (_, _) =>
        {
            _pressed = BarHit.None;
            UpdateHover(BarHit.None);   // ★ 走统一入口 ✓ 顺带把封面悬停动画收回去 ✓
        };
        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseWheel += OnWheel;
    }

    /// <summary>点击了一个可交互区域（在 UI 线程上触发）。</summary>
    public event Action<BarHit>? Clicked;

    /// <summary>请求跳转到 0..1 的比例位置。</summary>
    public event Action<double>? SeekRequested;

    /// <summary>滚轮（正数向上）在条上滚动。</summary>
    public event Action<int>? Wheeled;

    public Media.MediaSnapshot Snapshot => _snapshot;

    public BarHit Hovered => _hover;

    /// <summary>按内容算出来的宽度（DIP），宿主据此设定窗口宽度。</summary>
    public double BarWidth { get; private set; } = 320;

    /// <summary>封面在窗口内的矩形（DIP）。切来源提示要"左对齐到专辑封边"，靠它定位。</summary>
    public Rect ArtworkRectDip => new(_padX, Math.Max(0, (ActualHeight - _artSize) / 2), _artSize, _artSize);

    public void Update(Media.MediaSnapshot snapshot)
    {
        _snapshot = snapshot;

        // ★ 封面可能因为换歌/暂停而变成"没有封面"✗
        //   若此时鼠标正好停在封面上，`_hover` 还是 Artwork ⇒ 悬停效果会留着 ✗
        //   这里按新 snapshot 重新判一次 ✓（封面没了 ⇒ 目标变 0 ⇒ 动画收回 ✓）
        if (_hover == BarHit.Artwork && snapshot.Artwork is null)
        {
            SetArtHoverTarget(0.0);
        }

        Measure(Size.Empty);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>进度条上的临时位置（拖动时为 0..1，否则 -1）。</summary>
    public void SetDragRatio(double ratio)
    {
        _draggingProgressRatio = ratio;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- 测量

    protected override Size MeasureOverride(Size availableSize)
    {
        // 注意：这里**不能**用 availableSize.Width 做上限。availableSize 就是当前窗口宽度，
        // 拿它去 clamp 会形成反馈回路 —— 条只能缩不能长（换到长标题就被挤成一团、按钮压住文字）。
        // 内容需要的宽度在这里如实算出来，最终能不能放下由宿主按任务栏剩余空间决定。
        double height = double.IsInfinity(availableSize.Height) || availableSize.Height <= 0 ? 36 : availableSize.Height;

        // ★ 把**真实可用高度**存下来给 ComputeLayout 用（封面大小的上限要按它算 ✓）
        // 不能读 ActualHeight ✗ —— 布局过程中它不可靠（实测给了 58.7，而窗口其实只有 48 DIP ✗）
        _barHeightDip = height;
        BarWidth = ComputeLayout().Width;
        return new Size(BarWidth, height);
    }

    /// <summary>本次布局可用的高度（DIP，来自 MeasureOverride 的 availableSize，可靠 ✓）。</summary>
    private double _barHeightDip = 36;

    private BarLayout ComputeLayout()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string title = _snapshot.DisplayTitle;
        string artist = _snapshot.DisplayArtist;
        string position = Format(_snapshot.PositionAt(now));

        // ★ 全元素等比例缩放：设计基准高 36 DIP（原来条子只有 36 DIP 高）。
        // 现在条子高度改成"占满任务栏客户区"（约 48 DIP ✓ 见 TopMarginDip=0 ✓）
        // → 所有尺寸按 ActualHeight/36 放大 ✓ 视觉上整体"长大一圈" ✓
        // 夹在 0.75~1.45：任务栏特别矮时不会被裁 ✓ 特别高时也不会撑爆 ✓
        _contentScale = Math.Clamp(ActualHeight > 0 ? ActualHeight / 36.0 : 1.0, 0.75, 1.45);
        double S = _contentScale;

        _padX = PaddingX * S;
        _artTextGap = ArtTextGap * S;
        _progressWidth = ProgressWidth * S;
        _progressGap = ProgressGap * S;
        _timeGap = TimeGap * S;
        _buttonSize = ButtonSize * S;
        _buttonGap = ButtonGap * S;
        _progressHeight = ProgressHeight * S;
        // 歌词/文字区字号：在整体缩放之上再乘用户设置的百分比（用户要求"歌词字体大小可自定义" ✓）
        _titleSize = TitleSize * S * _fontScale;
        _artistSize = ArtistSize * S * _fontScale;
        _timeSize = TimeSize * S;

        string time = $"{position} / {Format(_snapshot.Duration)}";

        // ★ 标题/歌手/时间的三份排版**必须缓存** ✗
        // ComputeLayout 每帧都跑 ✓ 而 MakeText 每次都要字体整形（很贵 ✓）
        // → 60fps 连续重绘（滚动 / 逐字）时这 3 份就吃掉大半预算 ✓✓（用户实测"很卡" ✓）
        // 只在"文本或字号变了"时重建 ✓ 平时直接复用 ✓
        string textKey = $"{title}|{artist}|{time}|{_titleSize:0.##}|{_artistSize:0.##}|{_timeSize:0.##}";
        if (!string.Equals(textKey, _textCacheKey, StringComparison.Ordinal))
        {
            _cachedTitleText = MakeText(title, _titleSize, TitleBrush, TextMaxWidth, FontWeights.SemiBold);
            _cachedArtistText = MakeText(artist, _artistSize, ArtistBrush, TextMaxWidth, FontWeights.Normal);
            _cachedTimeText = MakeText(time, _timeSize, TimeBrush, 200, FontWeights.Normal);
            _textCacheKey = textKey;
        }

        FormattedText titleText = _cachedTitleText;
        FormattedText artistText = _cachedArtistText;
        FormattedText timeText = _cachedTimeText;

        // ★ 封面大小**完全独立于字号** ✓（用户要求"这两根要分开单独设置" ✓）
        // 基准是**固定值** ArtSize×整体缩放 ✓ —— 上一版我把基准设成"两行歌词总高" ✗
        // 结果字号一变封面还是跟着动 ✗（用户实测抓到 ✓）所以基准必须与文字无关 ✓
        //
        // 上限 = 条高 − 6 DIP ✓：**绝不允许超出条子**（超出会被裁 → 图片显示不全 → 用户以为是 bug ✓）
        double artWanted = ArtSize * S * _artScale;
        double artCeiling = Math.Max(10, _barHeightDip - (2 * S));   // 边距收到 2 DIP：能放得更大一点，但仍保证不会被裁 ✓
        _artSize = Math.Clamp(artWanted, 10, artCeiling);

        double textWidth = Math.Max(titleText.Width, artistText.Width);
        textWidth = Math.Clamp(textWidth + 2, TextMinWidth, TextMaxWidth);

        // ★ 有歌词时的文字区宽度 —— 分两种模式（用户要求：加一个"自动适配"开关 ✓）
        // 自动适配**开**（默认）：
        //   · 短句 → 条子收窄 ✓（AFM 那种"贴着内容"的观感 ✓）
        //   · 长句 → 放宽到"任务栏空档能给的上限"为止 ✓（再长交给跑马灯滚动 ✓）
        // 自动适配**关**：
        //   · 宽度 = 用户设的「歌词自定义宽度」固定值 ✓ 不随歌词长短变化 ✓
        // 下限 60 DIP ✓（避免短句把条子缩成一条线 ✓）
        // 诊断开关 RYUBAR_FORCE_MARQUEE=1 仍会把文字区压窄，用来强制触发跑马灯做性能验证。
        if (HasLyricsText)
        {
            if (_forceMarquee)
            {
                textWidth = 60;
            }
            else if (!_lyricsAutoFit)
            {
                // ★ 关闭自动适配 → 应用**自定义宽度** ✓（用户要求："当自动适配选项关闭后再应用自定义宽度" ✓）
                // 超出的长句走跑马灯滚动 ✓ 不会被裁 ✗
                textWidth = Math.Clamp(_lyricsWidthDip, TextMinWidth, 900);
            }
            else
            {
                // ★ 量歌词自然宽度 —— **必须缓存** ✗
                // 每帧重新测量要构造 2 个 FormattedText（含字体整形）✓ 很贵 ✓
                // 结果：滚动/逐字这种需要 60fps 连续重绘的场景直接掉帧（用户实测"很卡，帧率比较低" ✓✓）
                // 只在"文本或字号变了"时重算一次 ✓ 平时直接读缓存 ✓
                string cur = _lyrics?.Current?.Text ?? "";
                string nxt = _lyrics?.Next?.Text ?? "";
                string key = $"{cur}|{nxt}|{_titleSize:0.##}|{_artistSize:0.##}";
                if (!string.Equals(key, _lyricsNeedKey, StringComparison.Ordinal))
                {
                    double need = 0;
                    if (!string.IsNullOrWhiteSpace(cur))
                    {
                        need = Math.Max(need, MakeText(cur, _titleSize, TitleBrush, 4000, FontWeights.SemiBold).Width);
                    }
                    if (!string.IsNullOrWhiteSpace(nxt))
                    {
                        need = Math.Max(need, MakeText(nxt, _artistSize, ArtistBrush, 4000, FontWeights.Normal).Width);
                    }
                    _lyricsNeedWidth = need;
                    _lyricsNeedKey = key;
                }

                // ★ 自动模式的上限 = 任务栏空档能给多少 ✓（用户选定方案：自动模式不再让用户配上限 ✗）
                //   没测到空档时退回 900 DIP（视图自身的保守天花板 ✓），下限兜住 60 DIP ✓
                double cap = _autoFitCapDip > 0 ? _autoFitCapDip : 900;
                cap = Math.Max(cap, TextMinWidth);
                textWidth = _lyricsNeedWidth <= 0
                    ? Math.Min(_lyricsWidthDip, cap)
                    : Math.Clamp(_lyricsNeedWidth + (4 * S), 60, cap);
            }
        }

        // ★ 没有音源（空闲态）：文字区宽度归 0 ✗ 条子收缩到"只有封面"✓
        //   必须放在**所有文字宽度计算之后** ✗ 否则会被上面的歌词分支覆盖回去 ✓
        //   用户要求：这种状态只用音符图标占位，不显示歌词/进度条/上下首与播放键 ✓
        if (IsIdle)
        {
            textWidth = 0;
        }

        // 其余部件（封面 / 按钮 / 进度条 / 时间 / 留白）的总宽 —— 与文字区宽度无关。
        // 按用户开关累加：三个开关任意组合都要能用，所以宽度必须跟着变。
        double rightParts = 0;
        if (_showControls)
        {
            rightParts += (_buttonSize * 3) + (_buttonGap * 2);
        }
        if (_spectrum)
        {
            rightParts += SpectrumWidth * _contentScale;   // 频谱占宽（在歌词右侧 ✓）
        }
        if (ShowBarProgress)
        {
            rightParts += _progressGap + _progressWidth;
        }
        if (ShowTimeText)
        {
            rightParts += (ShowBarProgress ? _timeGap : _progressGap) + timeText.Width;
        }
        double rightGap = rightParts > 0 ? _progressGap : 0;

        double chrome = (_padX * 2) + _artSize + _artTextGap + rightGap + rightParts;
        ChromeWidthDip = (int)Math.Round(chrome);   // 当前配置的实际占宽（宿主用它算"这套配置下能放多大"✓）

        // 「必需部件」宽度 = 留白 + 封面 + 间距 + 时间（时间没有开关，永远在 ✓）
        // 用途：用户要求"最大限位 = 当前状态的最大值" ✓
        // 即**不管他开关哪些可选部件**（频谱 / 控制按钮 / 进度条），上限都按"最省配置"算，
        // 得到这台机器真正能达到的最大值 ✓ —— 限位不再随开关部件来回浮动 ✓
        MinChromeWidthDip = (int)Math.Round(
            (_padX * 2) + _artSize + _artTextGap + (ShowTimeText ? (_progressGap + timeText.Width) : 0));

        // 「安全区」整条的**可行最小宽度** = 其余部件总宽 + 文字区最小宽度（24 DIP），
        // 再**向下取整到整十**（例如 301 → 300）—— 用户要求"最小宽度设置成整数"，
        // 取整十后钳制结果和 ±20 步进都落在整齐的值上。
        // 低 1~9 DIP 由视图自身的下限兜底（最多被裁掉几像素，视觉无感），因此不会抖动。
        MinimumTotalWidthDip = (int)(Math.Floor((chrome + FixedTextMinWidthDip) / 10) * 10);

        // 固定宽度：整条 = 固定值。布局对 textWidth 线性，把差额喂给文字区即可精确落地。
        if (_forcedWidthDip > 0 && !_forceMarquee)
        {
            double effective = Math.Max(_forcedWidthDip, chrome + FixedTextMinWidthDip);
            textWidth = Math.Clamp(effective - chrome, FixedTextMinWidthDip, 900);
        }

        titleText.MaxTextWidth = textWidth;
        artistText.MaxTextWidth = textWidth;

        double width = (_padX * 2) + _artSize + _artTextGap + textWidth + rightGap + rightParts;

        BarLayout layout = new()
        {
            Width = Math.Floor(width),
            Title = titleText,
            Artist = artistText,
            Time = timeText,
            TextWidth = textWidth,
            TimeWidth = timeText.Width,
        };
        return layout;
    }

    private FormattedText MakeText(string text, double size, Brush brush, double maxWidth, FontWeight weight)
    {
        FormattedText formatted = new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // 顺序很重要：先取"未限制宽度"时的高度 = 单行行高，再限制宽度与高度。
        // 只给 MaxTextWidth 会让长文本自动换行，把后面的行挤出条外；
        // 必须同时给 MaxTextHeight，末尾的省略号才会落在第一行上。
        // 不要设 LineHeight：把它设成"自然行高 + 1"会让短文本量出 0x0（实测），
        // 而它并非实现单行省略所必需。
        double naturalLineHeight = Math.Ceiling(formatted.Height) + 1;
        formatted.MaxTextWidth = maxWidth;
        formatted.MaxTextHeight = naturalLineHeight;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        return formatted;
    }

    /// <summary>
    /// 右侧控件的横向位置。从右边缘往左排：时间 → 进度 → 按钮组（各自按开关决定是否排）。
    /// 被关掉的部件坐标置为 NaN，绘制与命中测试都据此跳过。
    /// 播放器不上报时间轴时（HasTimeline 为假），进度与时间都不画 —— 宁可少一块，也不显示编出来的进度。
    /// </summary>
    private RightSide ComputeRightSide(double w, double timeWidth)
    {
        RightSide side = new();
        double x = w - _padX;

        if (ShowTimeText)
        {
            side.TimeX = x - timeWidth;
            x = side.TimeX;
            side.ShowTime = true;
        }

        if (ShowBarProgress)
        {
            side.ProgressX = x - ((ShowTimeText ? _timeGap : 0) + _progressWidth);
            x = side.ProgressX;
            side.ShowProgress = true;
        }

        // 频谱排在"文字区"和"按钮组"之间（用户要求：频谱在歌词右侧 ✓）
        // ★ 空闲态不画频谱 ✓（没有音源时它全是 0，白占位置 ✓）
        double spectrumWidth = SpectrumWidth * _contentScale;
        if (_spectrum && !IsIdle)
        {
            side.SpectrumX = x - spectrumWidth;
            x = side.SpectrumX;
            side.ShowSpectrum = true;
        }

        // ★ 空闲态不画控制按钮 ✓（用户要求：不显示上下首与播放键 ✓）
        if (_showControls && !IsIdle)
        {
            side.NextX = x - ((side.ShowTime || side.ShowProgress || side.ShowSpectrum ? _progressGap : 0) + _buttonSize);
            side.PlayX = side.NextX - _buttonGap - _buttonSize;
            side.PrevX = side.PlayX - _buttonGap - _buttonSize;
            side.ShowControls = true;
        }

        if (!side.ShowProgress)
        {
            side.ProgressX = double.NaN;
        }
        if (!side.ShowTime)
        {
            side.TimeX = double.NaN;
        }
        if (!side.ShowSpectrum)
        {
            side.SpectrumX = double.NaN;
        }
        if (!side.ShowControls)
        {
            side.NextX = double.NaN;
            side.PlayX = double.NaN;
            side.PrevX = double.NaN;
        }
        return side;
    }

    private struct RightSide
    {
        public bool ShowProgress;
        public bool ShowTime;
        public bool ShowSpectrum;
        public bool ShowControls;
        public double TimeX;
        public double ProgressX;
        public double SpectrumX;
        public double NextX;
        public double PlayX;
        public double PrevX;
    }

    /// <summary>
    /// 把控件矩形描述成一行文本（窗口内像素坐标，给定 DPI 缩放）。
    /// 用途：让外部脚本/诊断按真实布局点击，而不是靠猜坐标。
    /// </summary>
    public string DescribeLayout(double scale)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 1 || h <= 1)
        {
            return "size=" + w.ToString("0") + "x" + h.ToString("0") + " (未布局)";
        }

        BarLayout layout = ComputeLayout();
        RightSide side = ComputeRightSide(w, layout.TimeWidth);

        string Px(double dip) => (dip * scale).ToString("0");
        string Range(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? "hidden" : Px(a) + ".." + Px(b);

        return string.Format(
            CultureInfo.InvariantCulture,
            "w={0} h={1} scale={2:0.##} | prev={3}..{4} play={5}..{6} next={7}..{8} progress={9} time={10} art=0..{11}"
            + " | text: title={12}x{13} artist={14}x{15} time={16}x{17} textW={18} timeline={19} canSeek={20}",
            Px(w), Px(h), scale,
            Px(side.PrevX), Px(side.PrevX + _buttonSize),
            Px(side.PlayX), Px(side.PlayX + _buttonSize),
            Px(side.NextX), Px(side.NextX + _buttonSize),
            Range(side.ProgressX, side.ProgressX + _progressWidth),
            Range(side.TimeX, w - _padX),
            Px(_padX + _artSize),
            layout.Title.Width.ToString("0.0"), layout.Title.Height.ToString("0.0"),
            layout.Artist.Width.ToString("0.0"), layout.Artist.Height.ToString("0.0"),
            layout.Time.Width.ToString("0.0"), layout.Time.Height.ToString("0.0"),
            layout.TextWidth.ToString("0.0"),
            _snapshot.HasTimeline ? "yes" : "no",
            _snapshot.CanSeek ? "yes" : "no");
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnRender(DrawingContext dc)
    {
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            RenderCore(dc);
        }
        finally
        {
            // 每帧渲染耗时（滚动是否顺滑的硬指标：远小于 16ms 才可能 60fps）。
            // 必须用 Stopwatch：TickCount64 只有 ~15.6ms 分辨率，会把所有帧都量成 0（踩过）。
            double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _renderSamples++;
            _renderTotalMs += elapsedMs;
            TraceRollFrame();   // 滚动期间逐帧计时（最大间隔 = 卡顿 ✓）
            if (elapsedMs > MaxRenderMilliseconds)
            {
                MaxRenderMilliseconds = elapsedMs;
            }
        }
    }

    private int _renderSamples;
    private double _renderTotalMs;

    /// <summary>平均每帧渲染耗时（毫秒）。</summary>
    public double AverageRenderMilliseconds => _renderSamples == 0 ? 0 : _renderTotalMs / _renderSamples;

    /// <summary>观测到的最大单帧渲染耗时（毫秒）。</summary>
    public double MaxRenderMilliseconds { get; private set; }

    /// <summary>重置渲染耗时统计（诊断用）。</summary>
    public void ResetRenderStats()
    {
        _renderSamples = 0;
        _renderTotalMs = 0;
        MaxRenderMilliseconds = 0;
    }

    private void RenderCore(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        Rect outer = new(0.5, 0.5, w - 1, h - 1);
        double radius = Math.Min(8, h / 2);
        dc.DrawRoundedRectangle(_chipBackground, _chipBorder, outer, radius, radius);

        BarLayout layout = ComputeLayout();
        RightSide side = ComputeRightSide(w, layout.TimeWidth);
        double cy = h / 2;
        double x = _padX;

        // 封面（环形进度画在它外圈 ✓ 外扩 3 DIP ✓ 不占横向空间）
        Rect art = new(x, cy - _artSize / 2, _artSize, _artSize);
        DrawArtwork(dc, art);
        if (ShowRingProgress)
        {
            DrawProgressRing(dc, art);
        }
        x += _artSize + _artTextGap;

        // 文字吃掉右侧所有部件左边的剩余空间。
        // 注意：三个开关全关时右侧没有任何部件 → PrevX/ProgressX/TimeX 都是 NaN ✗，
        // 直接拿去做 Min 会把文字宽度算成 NaN → 文字**画不出来**（实测踩到）→ 用右边缘兜底。
        double rightLimit = side.ShowControls
            ? side.PrevX
            : side.ShowProgress
                ? side.ProgressX
                : side.ShowTime
                    ? side.TimeX
                    : w - _padX;
        double textWidth = Math.Max(TextMinWidth, Math.Min(layout.TextWidth, rightLimit - _progressGap - x));

        // ★ 宽度动画帧**不要重设 MaxTextWidth** ✗（这是每帧 ~24ms 的元凶 ✓）
        //   原因：`FormattedText.MaxTextWidth` 的 setter 会让文本**重新整形**（重新决定换行与裁剪）✗
        //   而动画每一帧都要走这里 ⇒ 每帧重排一次文本 ⇒ 帧间隔 ~24ms（≈40fps）✗ 达不到 60fps ✗
        //   代价：动画期间文字的裁剪宽度停在上一次的值 ✓ 220ms 内看不出来 ✓ 动画一结束立刻恢复 ✓
        layout.Title.MaxTextWidth = textWidth;
        layout.Artist.MaxTextWidth = textWidth;

        // ★ 空闲态（没有音源）：**只画封面**（音符占位）✗
        //   用户要求：不显示歌词、进度条、上下首与播放键 ✓
        //   标题/歌手也不画 ✓ —— 否则会显示"没有正在播放的媒体"这类占位文字 ✗ 条子收不干净 ✓
        if (IsIdle)
        {
            return;   // 封面已经在上面画好了 ✓ 其余一律不画 ✓
        }

        // 有歌词就画歌词（当前行 + 下一句），否则画标题/歌手
        // ★ 换句时**向上滚动一行**（用户："改成向下滚动来切换到下句歌词，这样看着也更合理" ✓）
        // 做法：把整个歌词块**先下移一行行高**，再用 220ms CubicEaseOut 升回原位 ✓
        // 视觉：旧句往上退出、新句从下方"下一行"的位置升上来 ✓（提词器那种滚动 ✓ 语义也更贴合"到下一句" ✓）
        // 为什么不用淡入 ✗：滚动能表达"顺序推进" ✓ 淡入只是"换了内容" ✓ 用户要的是前者 ✓
        double roll = _lyricsRollAt == default
            ? 1.0
            : Math.Clamp((DateTime.Now - _lyricsRollAt).TotalMilliseconds / LyricRollDurationMs, 0, 1);
        bool rolling = roll < 1;
        double rollOffset = 0;
        if (rolling)
        {
            double eased = 1 - Math.Pow(1 - roll, 3);          // CubicEaseOut（与宽度缓动同一曲线 ✓）
            rollOffset = layout.Title.Height * 1.15 * (1 - eased);   // 行高按标题行高估 ✓
            IsKaraokeActive = true;                            // 滚动期间必须有 60fps 重绘驱动 ✓
            dc.PushTransform(new TranslateTransform(0, rollOffset));
        }

        if (DrawLyrics(dc, x, cy, h, textWidth))
        {
            // 歌词已画，继续画按钮与进度
        }
        else
        {
            double titleHeight = layout.Title.Height;
            double artistHeight = layout.Artist.Height;
            bool hasArtist = !string.IsNullOrWhiteSpace(_snapshot.DisplayArtist);

            // 两行放不下时只画标题（例如条被压得很矮时），任何情况下都不许溢出条外
            if (hasArtist && titleHeight + artistHeight > h - 2)
            {
                hasArtist = false;
            }

            if (hasArtist)
            {
                double blockHeight = titleHeight + artistHeight;
                double top = cy - blockHeight / 2;
                dc.DrawText(layout.Title, new Point(x, top));
                dc.DrawText(layout.Artist, new Point(x, top + titleHeight));
            }
            else
            {
                dc.DrawText(layout.Title, new Point(x, cy - titleHeight / 2));
            }
        }

        if (rolling)
        {
            dc.Pop();   // 与上面的 PushTransform 配对 ✓
        }

        if (side.ShowSpectrum)
        {
            DrawSpectrum(dc, side.SpectrumX, cy, h);
        }

        // 三个按钮（用户可以在设置里整组关掉）
        if (side.ShowControls)
        {
            DrawButton(dc, new Rect(side.PrevX, cy - _buttonSize / 2, _buttonSize, _buttonSize), BarHit.Previous,
                Glyph.Previous, _snapshot.CanPrevious);
            DrawButton(dc, new Rect(side.PlayX, cy - _buttonSize / 2, _buttonSize, _buttonSize), BarHit.PlayPause,
                _snapshot.IsPlaying ? Glyph.Pause : Glyph.Play, _snapshot.CanPlayPause);
            DrawButton(dc, new Rect(side.NextX, cy - _buttonSize / 2, _buttonSize, _buttonSize), BarHit.Next,
                Glyph.Next, _snapshot.CanNext);
        }

        // 条状进度：只在播放器真的上报了时间轴时才画。
        // 能跳转用强调色；不能跳转（QQ音乐/网易云/酷狗这类）用灰色 + 点击不做任何反应。
        if (side.ShowProgress)
        {
            double barRatio = CurrentRatio();
            double trackY = cy - _progressHeight / 2;
            Rect track = new(side.ProgressX, trackY, _progressWidth, _progressHeight);
            dc.DrawRoundedRectangle(TrackBrush, null, track, _progressHeight / 2, _progressHeight / 2);
            if (barRatio > 0)
            {
                double fillWidth = Math.Max(_progressHeight, _progressWidth * barRatio);
                Brush fill = _snapshot.CanSeek ? AccentBrush : AccentDisabledBrush;
                dc.DrawRoundedRectangle(fill, null, new Rect(side.ProgressX, trackY, fillWidth, _progressHeight), _progressHeight / 2, _progressHeight / 2);
            }
        }

        // 时间：只要曲目有时间轴就画（不论进度用条状还是环形，时间都是有意义的信息）
        if (side.ShowTime)
        {
            dc.DrawText(layout.Time, new Point(side.TimeX, cy - layout.Time.Height / 2));
        }
    }

    /// <summary>
    /// 画出封面轮廓（圆角矩形）的点列：从上边中点起、**顺时针**一圈、回到起点。
    /// 环形进度就是"封面图自己的边框" ✓ 所以轮廓必须与封面的圆角完全一致（用户明确要求 ✓）。
    /// </summary>
    private static List<Point> ArtOutline(Rect art, double radiusOverride = 0)
    {
        double radius = radiusOverride > 0
            ? radiusOverride
            : Math.Max(0.5, Math.Min(ArtRadius, Math.Min(art.Width, art.Height) / 2));
        double left = art.Left;
        double right = art.Right;
        double top = art.Top;
        double bottom = art.Bottom;
        List<Point> points = new(96);

        void Arc(double cx, double cy, double from, double to)
        {
            for (double angle = from; angle <= to + 0.001; angle += 6)
            {
                double rad = angle * Math.PI / 180;
                points.Add(new Point(cx + (radius * Math.Cos(rad)), cy + (radius * Math.Sin(rad))));
            }
        }

        points.Add(new Point((left + right) / 2, top));   // 上边中点（起点 = 12 点方向）
        points.Add(new Point(right - radius, top));
        Arc(right - radius, top + radius, -90, 0);        // 右上圆角
        points.Add(new Point(right, bottom - radius));
        Arc(right - radius, bottom - radius, 0, 90);      // 右下圆角
        points.Add(new Point(left + radius, bottom));
        Arc(left + radius, bottom - radius, 90, 180);     // 左下圆角
        points.Add(new Point(left, top + radius));
        Arc(left + radius, top + radius, 180, 270);       // 左上圆角
        points.Add(new Point((left + right) / 2, top));   // 闭合
        return points;
    }

    /// <summary>点 p 到封面轮廓的最近距离，以及它在轮廓上的归一化位置（0=上边中点，顺时针）。</summary>
    private static (double Distance, double Ratio) NearestOnOutline(List<Point> outline, Point p)
    {
        double bestDistance = double.MaxValue;
        double bestLength = 0;
        double travelled = 0;

        for (int i = 0; i < outline.Count - 1; i++)
        {
            Point a = outline[i];
            Point b = outline[i + 1];
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double segLength = Math.Sqrt((dx * dx) + (dy * dy));
            if (segLength <= 0.0001)
            {
                continue;
            }

            double t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / (segLength * segLength), 0, 1);
            double px = a.X + (t * dx);
            double py = a.Y + (t * dy);
            double distance = Math.Sqrt(((p.X - px) * (p.X - px)) + ((p.Y - py) * (p.Y - py)));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestLength = travelled + (t * segLength);
            }
            travelled += segLength;
        }

        return (bestDistance, travelled <= 0 ? 0 : Math.Clamp(bestLength / travelled, 0, 1));
    }

    /// <summary>
    /// 环形进度 = **封面图的边框**：沿封面轮廓（圆角矩形）画一圈，已播部分用强调色。
    /// 从 12 点方向起、顺时针推进；轨道（未播）用轨道色画整圈。
    /// </summary>
    private void DrawProgressRing(DrawingContext dc, Rect art)
    {
        Rect ring = RingRect(art);
        double radius = RingRadius(ring);

        // ★ **不再画深色底衬** ✗ —— 用户实测截图证实：那层黑 40%、又粗的描边
        //   把用户选的颜色**整个盖住** ✗ → "改颜色没用，一直是不透明的颜色" ✓✓
        // 现在：轨道 = 柔和浅灰细线 ✓ 已播 = 用户选的颜色且更粗 ✓ → 颜色就是视觉主角 ✓

        // 轨道（未播）：柔和的浅色轮廓（比已播细 ✓ 不抢戏 ✓ 又足够看清环在哪儿）
        Pen trackPen = new(RingTrackBrush, TrackThicknessDip * _contentScale);
        trackPen.Freeze();
        dc.DrawRoundedRectangle(null, trackPen, ring, radius, radius);

        double ratio = Math.Clamp(CurrentRatio(), 0, 1);

        // 诊断：每 2 秒记一次"环算出来的进度"（用于确认到底是没读到位、还是画得看不清）
        if ((DateTime.Now - _ringLogAt).TotalSeconds > 2)
        {
            _ringLogAt = DateTime.Now;
            Diagnostics.Log.Write("ring", $"画环：ratio={ratio:0.###} canSeek={_snapshot.CanSeek} canShow={_snapshot.CanShowProgress} " +
                $"位置={_snapshot.PositionAt(DateTimeOffset.Now).TotalSeconds:0}s 时长={_snapshot.Duration.TotalSeconds:0}s");
        }

        if (ratio <= 0.0005)
        {
            return;
        }

        List<Point> outline = ArtOutline(ring, radius);

        // ★ 环的已播色**永远用用户在设置里选的颜色** ✓
        // 原来这里跟条状进度共用一条规则："曲目不能跳转 → 用灰色" ✗
        // 结果：用户选了橙黄 ✓ 但曲目（QQ音乐这类）不能跳转 ✗ → 环一直是灰的 ✓
        // → 用户实测反馈"改颜色没用，一直是不透明的颜色" ✓✓（两个原因叠在一起：底衬 + 这条规则）
        Brush fill = RingAccent();

        // 满环：直接画整圈（部分路径在 ratio=1 时会退化成零长段 ✗）
        if (ratio >= 0.9995)
        {
            Pen fullPen = new(fill, _ringThicknessDip * _contentScale);
            fullPen.Freeze();
            dc.DrawRoundedRectangle(null, fullPen, ring, radius, radius);
            return;
        }

        // 按累计长度截取前 ratio 段
        double total = 0;
        List<double> lengths = new(outline.Count);
        for (int i = 0; i < outline.Count - 1; i++)
        {
            double dx = outline[i + 1].X - outline[i].X;
            double dy = outline[i + 1].Y - outline[i].Y;
            lengths.Add(Math.Sqrt((dx * dx) + (dy * dy)));
            total += lengths[i];
        }

        double target = total * ratio;
        List<Point> partial = new(outline.Count) { outline[0] };
        double walked = 0;
        for (int i = 0; i < lengths.Count; i++)
        {
            if (walked + lengths[i] > target)
            {
                double t = lengths[i] <= 0 ? 0 : (target - walked) / lengths[i];
                partial.Add(new Point(
                    outline[i].X + ((outline[i + 1].X - outline[i].X) * t),
                    outline[i].Y + ((outline[i + 1].Y - outline[i].Y) * t)));
                break;
            }

            partial.Add(outline[i + 1]);
            walked += lengths[i];
        }

        if (partial.Count < 2)
        {
            if ((DateTime.Now - _ringLogAt).TotalSeconds > 2)
            {
                _ringLogAt = DateTime.Now;   // ★ 不更新时间戳 ⇒ 节流形同虚设 ✗（这里原来漏了 ✓）
                Diagnostics.Log.Write("ring", $"已播段构建失败：partial={partial.Count} outline={outline.Count} target={target:0.#} total={total:0.#} ring={ring.Width:0.#}x{ring.Height:0.#}");
            }
            return;
        }
        // ★ 这一行原来是**每帧**写 ✗ —— 环可见时是 60fps 重绘 ⇒ 每秒几十到几百行日志 ✗
        //   后果：ryubar.log 几分钟就被刷到几百 KB 并触发轮转（实测一份日志 1 分钟 500KB）✗
        //   还白耗 IO 和 CPU ✓ 现在与上面的失败日志统一做**2 秒节流** ✓
        if ((DateTime.Now - _ringLogAt).TotalSeconds > 2)
        {
            _ringLogAt = DateTime.Now;
            Diagnostics.Log.Write("ring", $"画已播段：点数={partial.Count}/{outline.Count} 目标长={target:0.#}/{total:0.#} 色={_ringColor} 宽={_ringThicknessDip}");
        }

        // ★ 用**逐段 DrawLine** 画已播部分（不再用 PathGeometry 拼折线 ✗）
        // 原因：PathGeometry 那条路我日志确认"确实在执行、颜色也对" ✓ 但画面上看不到 ✗（原因未查明 ✓），
        // 而 DrawLine 是绘图原语 ✓ 和"能正常显示的轨道"用的是同一类调用 ✓ 不可能画不出来 ✓
        Pen pen = new(fill, _ringThicknessDip * _contentScale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        for (int i = 0; i < partial.Count - 1; i++)
        {
            dc.DrawLine(pen, partial[i], partial[i + 1]);
        }
    }

    /// <summary>
    /// **环的**轨道色：极暗（白色 12% 透明）。
    /// 之前用 25% ✗ 在暗底上仍是"中灰"，而用户的强调色 #7C8BA5 也是中灰 ✗ →
    /// 2 DIP 细线上两者亮度几乎一样 → 用户直接说"颜色我都看不出来" ✓✓ 所以压到 12% ✓
    /// </summary>
    private static readonly Brush RingTrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));

    /// <summary>环的深色底衬（黑色 38%）：保证在**亮封面**上也能看清环与进度。</summary>
    private static readonly Brush RingUnderlayBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)));

    /// <summary>环的"不可跳转"色：暗于已播、亮于轨道（白色 40%）。</summary>
    private static readonly Brush RingDisabledBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));

    private Color _ringAccentSource;
    private Brush? _ringAccent;

    /// <summary>环进度诊断日志的节流时间戳（每 2 秒最多一条）。</summary>
    private DateTime _ringLogAt;

    /// <summary>
    /// 环的已播色 = **当前主题色**（`SystemColors.AccentColor` 直接取，不提亮）。
    /// 用户要求："改成当前主题色试试" ✓
    /// （之前我按 35% 混白提亮 ✗ —— 虽然更亮，但已经不是主题色了 ✓）
    /// 深色底衬保留 ✓ 它是"看得见"的关键 ✓ 与颜色无关 ✓
    /// （系统强调色可能被换壁纸引擎改掉 ✓ 所以按当前值缓存 ✓ 变了就重建 ✓）
    /// </summary>
    private Brush RingAccent()
    {
        // 用户在设置里选了具体颜色 → 用它（与歌词配色同一套预设）
        Color baseColor = string.Equals(_ringColor, "accent", StringComparison.OrdinalIgnoreCase)
            ? SystemColors.AccentColor
            : ParseColor(_ringColor, SystemColors.AccentColor);

        // 不透明度：用户单独可调（0~100 %），与颜色相乘
        byte alpha = (byte)Math.Round(255.0 * _ringOpacity / 100.0);
        Color chosen = Color.FromArgb(
            (byte)Math.Round(baseColor.A * alpha / 255.0),
            baseColor.R,
            baseColor.G,
            baseColor.B);

        if (_ringAccent is null || _ringAccentSource != chosen)
        {
            _ringAccentSource = chosen;
            _ringAccent = Freeze(new SolidColorBrush(chosen));
        }
        return _ringAccent;
    }

    /// <summary>解析 `#RRGGBB` / `#AARRGGBB`；失败返回 fallback。</summary>
    private static Color ParseColor(string text, Color fallback)
    {
        try
        {
            string hex = text.TrimStart('#');
            if (hex.Length == 6)
            {
                return Color.FromRgb(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            }
            if (hex.Length == 8)
            {
                return Color.FromArgb(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16),
                    Convert.ToByte(hex.Substring(6, 2), 16));
            }
        }
        catch
        {
            // 手写坏值就用 fallback
        }
        return fallback;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>环的路径矩形已经是最终路径（不再需要内缩半个笔宽）。</summary>
    private Rect ArtClipRect(Rect art)
    {
        return RingRect(art);
    }

    /// <summary>
    /// 画歌词。有歌词且当前行非空时返回 true（此时标题/歌手不再画）。
    ///
    /// 性能要点（参照实现的做法）：文字**每行只排版一次并缓存**，每帧只改变换与裁剪。
    /// 逐字高亮是"整行画两遍 + 裁剪到已唱宽度"，而不是每字一个 FormattedText ——
    /// 后者每帧要构造上百个 FormattedText（含字体整形），60fps 下必卡（实测踩过，用户明确反馈"极其卡顿"）。
    /// </summary>
    private bool DrawLyrics(DrawingContext dc, double x, double cy, double h, double available)
    {
        IsMarqueeActive = false;
        IsKaraokeActive = false;
        LyricsView? lyrics = _lyrics;
        if (lyrics?.Current is not { } current || string.IsNullOrWhiteSpace(current.Text))
        {
            return false;
        }

        bool twoLines = _lyricsLines >= 2 && lyrics.Next is { } next && !string.IsNullOrWhiteSpace(next.Text);
        double currentSize = _titleSize;
        double currentHeight = currentSize * 1.32;
        double nextSize = _artistSize;
        double nextHeight = nextSize * 1.24;

        if (twoLines && currentHeight + nextHeight <= h - 2)
        {
            double top = cy - (currentHeight + nextHeight) / 2;
            DrawCachedLyricLine(dc, _cacheCurrent, current, x, top, available, currentSize,
                FontWeights.SemiBold, perWordAllowed: true, BrushFromColor(_lyricsTextColor, TitleBrush));
            DrawCachedLyricLine(dc, _cacheNext, lyrics.Next!, x, top + currentHeight, available, nextSize,
                FontWeights.Normal, perWordAllowed: false, BrushFromColor(_lyricsNextLineColor, ArtistBrush));
        }
        else
        {
            DrawCachedLyricLine(dc, _cacheCurrent, current, x, cy - currentHeight / 2, available, currentSize,
                FontWeights.SemiBold, perWordAllowed: true, BrushFromColor(_lyricsTextColor, TitleBrush));
        }
        return true;
    }

    private void DrawCachedLyricLine(
        DrawingContext dc,
        CachedLyricLine cache,
        LyricsLineView line,
        double x,
        double rowTop,
        double available,
        double size,
        FontWeight weight,
        bool perWordAllowed,
        Brush textBrush)
    {
        Brush highlightBrush = BrushFromColor(_lyricsHighlightColor, AccentBrush);
        CachedLyricLine cached = EnsureCache(cache, line, size, weight, available,
            perWordAllowed && _lyricsPerWord && line.Syllables is { Count: > 0 },
            textBrush,
            highlightBrush);

        bool scroll = _lyricsMarquee && cached.Width > available - 4;
        LastAvailableDip = available;
        LastCachedWidth = cached.Width;
        double offset = 0;
        if (scroll)
        {
            double elapsed = (DateTime.Now - _marqueeAnchor).TotalSeconds - MarqueeIntroSeconds;
            offset = elapsed <= 0 ? 0 : Math.Min(elapsed * MarqueeSpeedDipPerSecond, cached.Width + 24);
            if (perWordAllowed && offset < cached.Width + 24)
            {
                IsMarqueeActive = true;
            }
        }

        // 播放位置**每帧现算**（而不是用宿主塞进来的固定值）：
        // 宿主只在 250ms 的进度定时器里更新一次位置，逐字高亮的粒度是 100~300ms，
        // 用固定值会让高亮按 250ms 一跳 —— 这就是"逐字和音频不同步"的来源（实测）。
        int positionMs = (int)_snapshot.PositionAt(DateTimeOffset.Now).TotalMilliseconds;
        double sungWidth = cached.SungWidth(positionMs);

        // 还在一行内逐字推进 → 需要 60fps 连续重画（滚到头/唱完就停）
        if (perWordAllowed && cached.HasPieces && sungWidth < cached.Width - 0.5)
        {
            IsKaraokeActive = true;
        }

        double gap = scroll ? cached.Width + 24 : 0;
        int copies = scroll ? 2 : 1;

        // 对齐偏移：**不滚动**时按设置把整行推到左/中/右（滚动时从左边开始，对齐无意义）
        double alignOffset = 0;
        if (!scroll)
        {
            alignOffset = _lyricsAlignment switch
            {
                1 => (available - cached.Width) / 2,
                2 => available - cached.Width,
                _ => 0,
            };
            if (alignOffset < 0)
            {
                alignOffset = 0;
            }
        }

        dc.PushClip(new RectangleGeometry(new Rect(x - 1, rowTop - 2, available + 2, size * 1.9)));
        for (int copy = 0; copy < copies; copy++)
        {
            double originX = x + alignOffset - offset + (copy * gap);
            dc.PushTransform(new TranslateTransform(originX, rowTop));
            dc.DrawText(cached.Dim, new Point(0, 0));
            if (sungWidth > 0 && cached.HasPieces)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, -2, sungWidth, size * 1.9)));
                dc.DrawText(cached.Bright, new Point(0, 0));
                dc.Pop();
            }
            dc.Pop();
        }
        dc.Pop();
    }

    /// <summary>歌词行的排版缓存：文字只排一次，逐字位置只算一次，之后每帧只改变换/裁剪。</summary>
    private sealed class CachedLyricLine
    {
        public string Key = "";
        public FormattedText Dim = null!;
        public FormattedText Bright = null!;
        public double Width;
        public bool HasPieces;
        public List<(int StartMs, int EndMs, double X, double Width)> Pieces = new();

        /// <summary>已唱宽度（按逐字时间轴插值；只有行级数据时按整行比例）。</summary>
        public double SungWidth(int positionMs)
        {
            if (Pieces.Count == 0)
            {
                return 0;
            }

            double last = 0;
            foreach ((int start, int end, double pieceX, double pieceWidth) in Pieces)
            {
                if (positionMs < start)
                {
                    return last;
                }
                if (positionMs < end)
                {
                    double fraction = end > start ? (positionMs - start) / (double)(end - start) : 1;
                    return pieceX + (pieceWidth * Math.Clamp(fraction, 0, 1));
                }
                last = pieceX + pieceWidth;
            }
            return last;
        }
    }

    private CachedLyricLine EnsureCache(
        CachedLyricLine cache,
        LyricsLineView line,
        double size,
        FontWeight weight,
        double available,
        bool withPieces,
        Brush textBrush,
        Brush highlightBrush)
    {
        bool perWord = _lyricsPerWord;
        string key = $"{line.Text}|{size:0.##}|{weight.ToOpenTypeWeight()}|{withPieces}|{line.Syllables?.Count ?? 0}|{_lyricsTextColor}|{_lyricsHighlightColor}";
        if (cache.Key == key)
        {
            return cache;
        }

        cache.Key = key;
        cache.Dim = MakeText(line.Text, size, textBrush, 8000, weight);
        cache.Bright = MakeText(line.Text, size, highlightBrush, 8000, weight);
        cache.Width = cache.Dim.Width;
        cache.Pieces = new List<(int, int, double, double)>();
        cache.HasPieces = false;

        if (perWord && withPieces && line.Syllables is { Count: > 0 } syllables)
        {
            // 逐字位置：先各自量宽度，再**整体缩放**到与整行实测宽度一致。
            // 必须缩放：单个字/词单独排版时没有连字与字距上下文，累加值通常略小于整行宽度，
            // 结果就是行尾一小段永远高亮不到（表现为"逐字卡在最后一个词"，实测踩过）。
            List<(int StartMs, int EndMs, double Width)> raw = new(syllables.Count);
            double total = 0;
            foreach (LyricSyllable syllable in syllables)
            {
                if (string.IsNullOrEmpty(syllable.Text))
                {
                    continue;
                }
                FormattedText measure = MakeText(syllable.Text, size, textBrush, 8000, weight);
                raw.Add((syllable.StartMs, syllable.EndMs, measure.Width));
                total += measure.Width;
            }

            if (raw.Count > 0)
            {
                double scale = total > 0.01 ? cache.Width / total : 1;
                double cursor = 0;
                foreach ((int startMs, int endMs, double width) in raw)
                {
                    double scaled = width * scale;
                    cache.Pieces.Add((startMs, endMs, cursor, scaled));
                    cursor += scaled;
                }
                cache.HasPieces = true;
            }
        }

        _ = available;
        return cache;
    }

    private readonly CachedLyricLine _cacheCurrent = new();
    private readonly CachedLyricLine _cacheNext = new();

    /// <summary>
    /// 设置封面悬停的目标值并启动过渡（用户："变色过程需要动画"✓）。
    /// ★ 只有值真的变了才启动 ✗ 否则每次 MouseMove 都重启动画 ⇒ 进度永远重来 ✗
    ///   （这个坑在宽度缓动上踩过一次 ✓ 这里直接避开 ✓）
    /// </summary>
    private void SetArtHoverTarget(double target)
    {
        if (Math.Abs(target - _artHoverTarget) < 0.001)
        {
            return;
        }

        _artHoverTarget = target;
        _artHoverFrom = _artHoverT;
        _artHoverStart = DateTime.Now;

        _artHoverTimer ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _artHoverTimer.Tick -= OnArtHoverTick;
        _artHoverTimer.Tick += OnArtHoverTick;
        _artHoverTimer.Start();
    }

    private void OnArtHoverTick(object? sender, EventArgs e)
    {
        double t = (DateTime.Now - _artHoverStart).TotalMilliseconds / ArtHoverDurationMs;
        if (t >= 1)
        {
            _artHoverTimer!.Stop();
            _artHoverT = _artHoverTarget;   // 精确落到目标 ✓（避免缓动留下 0.9999 的浮点残留 ✗）
            InvalidateVisual();
            return;
        }

        double eased = 1 - Math.Pow(1 - t, 3);   // CubicEaseOut（与宽度缓动、歌词滚动同一曲线 ✓）
        _artHoverT = _artHoverFrom + ((_artHoverTarget - _artHoverFrom) * eased);
        InvalidateVisual();
    }

    /// <summary>
    /// 统一更新悬停命中的地方 ✗（原来 `_hover` 在三个地方各写一遍 ✓ 现在都在这里 ✓）。
    /// 顺带驱动封面的悬停动画 ✓
    /// </summary>
    private void UpdateHover(BarHit hit)
    {
        if (hit != _hover)
        {
            _hover = hit;
            InvalidateVisual();
        }

        // ★ 只有"真的有封面"才做放大/高亮/图标 ✓（没封面时放大会很怪 ✗）
        SetArtHoverTarget(hit == BarHit.Artwork && _snapshot.Artwork is not null ? 1.0 : 0.0);
    }

    private void DrawArtwork(DrawingContext dc, Rect art)
    {
        ImageSource? source = _snapshot.Artwork;

        // ★ 封面悬停的进度（没封面就恒为 0 ⇒ 一切效果都不出现 ✓）
        double hover = source is null ? 0.0 : _artHoverT;

        // ★ "微微放大"：以**中心**为基准 ✓ 1.0 → 1.08（8% 是肉眼明显但不夸张的量 ✓）
        Rect drawn = art;
        if (hover > 0.001)
        {
            double grow = 1.0 + (0.08 * hover);
            double dx = art.Width * (grow - 1.0) / 2.0;
            double dy = art.Height * (grow - 1.0) / 2.0;
            drawn = Rect.Inflate(art, dx, dy);
        }

        if (source == null)
        {
            // ★ 没有封面：**只画音符** ✗ 不画那个浅白圆角方块 ✓
            //   用户："像这种只显示图标，不填充其他部分" ✓
            //   原来"浅白圆角矩形 + 音符"看起来像"一个空封面"，显眼又多余 ✗
            Geometry note = Glyph.Note(drawn);
            dc.DrawGeometry(NotePlaceholderBrush, null, note);
            return;
        }

        double radius = Math.Min(ArtRadius, drawn.Height / 2);

        // ★ 裁剪到**放大后的**矩形 ✗ 不能裁到原矩形 ✓（否则放大被裁掉 = 看不出放大 ✗）
        dc.PushClip(new RectangleGeometry(drawn, radius, radius));
        dc.DrawImage(source, drawn);
        if (hover > 0.001)
        {
            // 叠一层浅亮：这就是"高亮效果"✓ 透明度跟随 hover 缓动 ⇒ 变色过程有动画 ✓
            dc.PushOpacity(hover);
            dc.DrawRectangle(ArtHoverTint, null, drawn);
            dc.Pop();
        }
        dc.Pop();

        // ★ 图标的可见度（用户："当歌曲暂停时封面图标要有个反馈，比如封面上显示一个播放图标"✓）
        //   播放中 → 跟随悬停 ✓（常态保持干净 ✗ 不挂图标）
        //   已暂停 → **常驻**显示 ▶ ✓（一眼就能看出"现在没在放"✓ 也让"点它继续"变得显然 ✓）
        //   注意：放大和高亮仍然只跟 hover ✓ 暂停时封面不会变大 ✓（用户只要求图标反馈 ✓）
        //   ★ 空闲态（没有音源）不给图标 ✗ —— 此时只有音符占位，再叠个播放三角就是误导 ✓
        double glyphAlpha = IsIdle ? 0.0 : (_snapshot.IsPlaying ? hover : 1.0);

        if (glyphAlpha > 0.001)
        {
            // ★ 中心按钮：播放中显示 ⏸（点了会暂停 ✓）、已暂停显示 ▶（点了会播放 ✓）
            //   语义 = "点下去会发生什么" ✓ 和右侧控制按钮一致 ✓
            //   样式：淡黑半透明圆底 + 白色图标 ✓ 刻意做小（0.38）× 淡（圆底 0.76）✓
            double size = drawn.Height * 0.38;
            double cx0 = drawn.X + (drawn.Width / 2);
            double cy0 = drawn.Y + (drawn.Height / 2);
            Rect glyphBox = new(cx0 - (size / 2), cy0 - (size / 2), size, size);

            // 暂停时的常驻图标略微收一点不透明度 ✓ 不至于像"永远有个按钮贴在那儿" ✗
            dc.PushOpacity(glyphAlpha * (_snapshot.IsPlaying ? 1.0 : 0.92));
            dc.DrawEllipse(ArtGlyphBackdrop, null, new Point(cx0, cy0), size * 0.76, size * 0.76);
            Geometry glyph = _snapshot.IsPlaying ? Glyph.Pause(glyphBox) : Glyph.Play(glyphBox);
            dc.DrawGeometry(ArtGlyphBrush, null, glyph);
            dc.Pop();
        }
    }

    private void DrawButton(DrawingContext dc, Rect box, BarHit hit, Func<Rect, Geometry> glyph, bool enabled)
    {
        bool hovered = _hover == hit && enabled;
        bool pressed = _pressed == hit && enabled;

        if (hovered || pressed)
        {
            dc.DrawRoundedRectangle(pressed ? PressedBrush : HoverBrush, null, box, ButtonRadius, ButtonRadius);
        }

        double inset = pressed ? 1.0 : 0;
        Rect glyphBox = Rect.Inflate(box, -inset, -inset);
        Geometry shape = glyph(glyphBox);
        Brush brush = enabled ? GlyphBrush : GlyphDisabledBrush;
        if (pressed)
        {
            dc.PushOpacity(0.85);
        }
        dc.DrawGeometry(brush, null, shape);
        if (pressed)
        {
            dc.Pop();
        }
    }

    // ---------------------------------------------------------------- 输入

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point point = e.GetPosition(this);

        if (_pressed == BarHit.Progress && _snapshot.CanSeek)
        {
            SetDragRatio(RatioFromPoint(point));
            return;
        }

        BarHit hit = HitTestBar(point);
        UpdateHover(hit);   // ★ 统一入口 ✓ 顺带驱动封面悬停动画 ✓
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Point point = e.GetPosition(this);
        BarHit hit = HitTestBar(point);
        UpdateHover(hit);
        _pressed = hit;

        if (hit == BarHit.Progress && _snapshot.CanSeek && (ShowBarProgress || ShowRingProgress))
        {
            SetDragRatio(RatioFromPoint(point));
        }
        InvalidateVisual();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        Point point = e.GetPosition(this);
        BarHit hit = HitTestBar(point);
        BarHit pressed = _pressed;
        _pressed = BarHit.None;

        // 进度条上的点击/拖动一律上报比例，**是否真的跳转由上层按播放器能力决定**
        // （不能跳转的来源根本不会走到这里：命中测试已经把它排除了）
        if (pressed == BarHit.Progress && hit == BarHit.Progress && _snapshot.CanSeek)
        {
            double ratio = _draggingProgressRatio >= 0 ? _draggingProgressRatio : RatioFromPoint(point);
            SetDragRatio(-1);
            if (ratio >= 0)
            {
                SeekRequested?.Invoke(ratio);
            }
            return;
        }

        SetDragRatio(-1);
        InvalidateVisual();

        if (pressed != BarHit.None && pressed == hit)
        {
            Clicked?.Invoke(pressed);
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Wheeled?.Invoke(e.Delta);
        e.Handled = true;
    }

    private double RatioFromPoint(Point point)
    {
        double w = ActualWidth;
        double h = ActualHeight;

        // ① 环形进度优先：点/拖在封面**边框**附近 → 按沿轮廓的位置算进度
        if (ShowRingProgress && RingSeekEnabled)
        {
            Rect ringArt = new(_padX, Math.Max(0, (h - _artSize) / 2), _artSize, _artSize);
            (double ringDistance, double ringRatio) = NearestOnOutline(ArtOutline(ringArt), point);
            if (ringDistance <= RingHitToleranceDip)
            {
                return ringRatio;
            }
        }

        // ② 条状进度：按横坐标算
        RightSide side = ComputeRightSide(w, ComputeLayout().TimeWidth);
        if (!side.ShowProgress)
        {
            return -1;
        }
        double ratio = (point.X - side.ProgressX) / _progressWidth;
        return Math.Clamp(ratio, 0, 1);
    }

    /// <summary>环上可拖动跳转是否启用（用户开关 + 曲目可跳转）。</summary>
    private bool RingSeekEnabled => _ringSeek && _snapshot.CanSeek;

    private const double RingHitToleranceDip = 6;

    /// <summary>命中的区域。</summary>
    public BarHit HitTestBar(Point point)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 1 || h <= 1 || point.X < 0 || point.Y < 0 || point.X > w || point.Y > h)
        {
            return BarHit.None;
        }

        double cy = h / 2;

        // 环形进度先判：环就是封面**边框**本身，先判才不会把它算成 Artwork
        if (ShowRingProgress && RingSeekEnabled)
        {
            Rect ringArt = new(_padX, Math.Max(0, (h - _artSize) / 2), _artSize, _artSize);
            (double ringDistance, _) = NearestOnOutline(ArtOutline(ringArt), point);
            if (ringDistance <= RingHitToleranceDip)
            {
                return BarHit.Progress;
            }
        }

        double x = _padX;
        if (point.X >= x && point.X <= x + _artSize)
        {
            return BarHit.Artwork;
        }
        x += _artSize + _artTextGap;

        BarLayout layout = ComputeLayout();
        RightSide side = ComputeRightSide(w, layout.TimeWidth);

        if (side.ShowControls
            && point.X >= side.PrevX - _buttonGap / 2 && point.X <= side.NextX + _buttonSize + _buttonGap / 2)
        {
            double top = cy - _buttonSize / 2;
            double bottom = top + _buttonSize;
            if (point.Y >= top - 2 && point.Y <= bottom + 2)
            {
                if (point.X < side.PlayX - _buttonGap / 2)
                {
                    return BarHit.Previous;
                }
                if (point.X < side.NextX - _buttonGap / 2)
                {
                    return BarHit.PlayPause;
                }
                return BarHit.Next;
            }
        }

        // 不能跳转时进度条区域**连命中都不给**：悬停无反馈、点击落到文字区（什么也不做），
        // 免得出现"看着能点、点了没反应"的错觉。
        if (side.ShowProgress && _snapshot.CanSeek
            && point.X >= side.ProgressX - 4 && point.X <= side.ProgressX + _progressWidth + 4)
        {
            return BarHit.Progress;
        }

        return BarHit.Text;
    }

    // ---------------------------------------------------------------- 工具

    internal static string Format(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
        {
            freezable.Freeze();
        }
        return freezable;
    }

    private struct BarLayout
    {
        public double Width;
        public FormattedText Title;
        public FormattedText Artist;
        public FormattedText Time;
        public double TextWidth;
        public double TimeWidth;
    }
}
