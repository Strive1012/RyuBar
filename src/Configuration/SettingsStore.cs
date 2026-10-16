using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyuBar.Configuration;

/// <summary>淡入/淡出相关设置。单位毫秒；0 表示该动作瞬时完成。</summary>
public sealed class FadeSettings
{
    /// <summary>露出时是否淡入。</summary>
    public bool FadeInEnabled { get; set; } = true;

    /// <summary>淡入时长。</summary>
    public int FadeInMilliseconds { get; set; } = 150;

    /// <summary>淡入延后量：等任务栏自己的露出动画画完再开始，避开 DWM 合成抢占。</summary>
    public int FadeInDelayMilliseconds { get; set; } = 150;

    /// <summary>隐藏时是否淡出。</summary>
    public bool FadeOutEnabled { get; set; } = true;

    /// <summary>淡出时长。</summary>
    public int FadeOutMilliseconds { get; set; } = 150;

    public void Normalize()
    {
        FadeInMilliseconds = Math.Clamp(FadeInMilliseconds, 0, 2000);
        FadeInDelayMilliseconds = Math.Clamp(FadeInDelayMilliseconds, 0, 2000);
        FadeOutMilliseconds = Math.Clamp(FadeOutMilliseconds, 0, 2000);
    }
}

/// <summary>
/// 各播放器的内存读取器参数。key 是来源标识（如 netease），值是模块内偏移（十六进制）。
///
/// 为什么可以持久化：模块基址每次启动都会变（ASLR），但**模块内偏移**对同一个播放器版本是稳定的。
/// 存下来之后，即使下次启动时歌曲处于暂停（自校准需要"数值在动"，暂停时校准不出来），
/// 也能直接读到真实位置 —— 这直接影响进度条显示的准确度。
/// </summary>
public sealed class ReaderSettings
{
    public string NetEasePositionOffset { get; set; } = "";

    public void Normalize()
    {
        NetEasePositionOffset ??= "";
    }
}

/// <summary>滚轮手势绑定的动作。取值与参照实现同名同义。</summary>
public enum WheelActionKind
{
    /// <summary>不响应这个手势。</summary>
    None,

    /// <summary>上一首 / 下一首。</summary>
    PreviousNext,

    /// <summary>循环切换声音来源。</summary>
    SwitchSource,
}

/// <summary>
/// 交互设置。两个手势分别可绑：普通滚轮（primary）与带修饰键的滚轮（chord）。
/// 参照实现里就是 <c>interaction.primaryWheelAction</c> / <c>chordWheelAction</c>。
/// </summary>
public sealed class InteractionSettings
{
    /// <summary>普通滚轮（光标在条上）。默认与参照实现一致：上下首。</summary>
    public string PrimaryWheelAction { get; set; } = "previousNext";

    /// <summary>Ctrl + 滚轮。默认切换声音来源。</summary>
    public string ChordWheelAction { get; set; } = "switchSource";

    /// <summary>切换来源时的提示窗口停留时间。</summary>
    public int TooltipMilliseconds { get; set; } = 1800;

    /// <summary>
    /// 提示落点。取值与参照实现的通知位置同名同义，外加一个 <c>aboveArtwork</c>：
    ///   * <c>aboveArtwork</c> —— 左边缘对齐专辑封面、浮在媒体条上方（默认）；
    ///   * <c>topLeft</c> / <c>topCenter</c> / <c>topRight</c> —— 屏幕工作区上排；
    ///   * <c>bottomLeft</c> / <c>bottomCenter</c> / <c>bottomRight</c> —— 屏幕工作区下排。
    /// </summary>
    public string TooltipPosition { get; set; } = "aboveArtwork";

    // 这两个是给代码用的解析结果，不参与序列化（否则设置文件里会多出 primaryKind/chordKind）
    [System.Text.Json.Serialization.JsonIgnore]
    public WheelActionKind PrimaryKind => Parse(PrimaryWheelAction, WheelActionKind.PreviousNext);

    [System.Text.Json.Serialization.JsonIgnore]
    public WheelActionKind ChordKind => Parse(ChordWheelAction, WheelActionKind.SwitchSource);

    private static WheelActionKind Parse(string? value, WheelActionKind fallback)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "none" or "off" or "disabled" or "" => WheelActionKind.None,
            "previousnext" or "track" or "skip" => WheelActionKind.PreviousNext,
            "switchsource" or "source" => WheelActionKind.SwitchSource,
            _ => fallback,
        };
    }

    public void Normalize()
    {
        PrimaryWheelAction ??= "previousNext";
        ChordWheelAction ??= "switchSource";
        TooltipPosition ??= "aboveArtwork";
        TooltipMilliseconds = Math.Clamp(TooltipMilliseconds, 300, 10000);
    }
}

/// <summary>
/// 歌词显示设置。凡是"用户可以选"的都放这里（设置窗口以后直接读这份）。
/// </summary>
public sealed class LyricsSettings
{
    /// <summary>显示行数：1 = 只显示当前行；2 = 当前行 + 下一句。</summary>
    public int Lines { get; set; } = 2;

    /// <summary>逐字高亮。只在歌词源真的给了逐字时间轴时才生效（行级歌词绝不猜）。</summary>
    public bool PerWordHighlight { get; set; } = true;

    /// <summary>长行跑马灯（不溢出时自动用省略号）。</summary>
    public bool Marquee { get; set; } = true;

    /// <summary>
    /// ★「歌词自定义宽度」（DIP，默认 280 约合 21 个汉字）——
    /// **只在 <see cref="AutoFitWidth"/> 关闭时生效**：文字区用这个固定宽度，超出的长句交给跑马灯滚动。
    /// 自动适配开启时宽度由软件自己算（上限取决于任务栏空档），本值不参与计算 ✓
    /// </summary>
    public int WidthDip { get; set; } = 280;

    /// <summary>
    /// 歌词宽度**自动适配**（默认开 = 与一直以来的观感完全一致 ✓）。
    /// 开 = 文字区宽度跟着当前歌词长短实时变化（短句收窄、长句放宽）。
    /// 关 = 用 <see cref="WidthDip"/> 作为固定宽度。
    /// 设置界面上它们**互斥联动**（自动适配开启时"歌词自定义宽度"变灰）；
    /// 「条子宽度 = 固定」时两者**一起变灰**——那种情况下文字区宽度由整条宽度反推，这两个值都会被覆盖 ✗
    /// </summary>
    public bool AutoFitWidth { get; set; } = true;

    /// <summary>未唱部分（主文字）颜色，形如 #RRGGBB 或 #AARRGGBB。</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>已唱部分（逐字高亮）颜色。</summary>
    public string HighlightColor { get; set; } = "#4CC2FF";

    /// <summary>下一句的颜色（通常更暗一些）。</summary>
    public string NextLineColor { get; set; } = "#8AFFFFFF";

    /// <summary>
    /// 是否丢弃"制作信息行"（作词/作曲/编曲/制作人等）。
    /// 静置层只有两行位置，这类行会把正文挤出可视区，所以默认丢弃（与参照实现默认一致）。
    /// 分类是启发式的：命中比例过高、或过滤后会清空时，整体放弃过滤（宁多显示一行，也不清空歌词）。
    /// </summary>
    public bool FilterInfoLines { get; set; } = true;

    /// <summary>
    /// 歌词对齐：left / center / right。
    /// 只在"这一行短于文字区、不需要滚动"时生效；长行进入跑马灯时总是从左边开始（此时对齐无意义）。
    /// </summary>
    public string Alignment { get; set; } = "left";

    /// <summary>
    /// 歌词/文字区字号倍率（百分比，80~150）。
    /// 100 = 标准 ✓；封面高度会自动跟着两行歌词的实际总高走 ✓（所以字号改了封面也对齐 ✓）
    /// </summary>
    public int FontScalePercent { get; set; } = 100;

    /// <summary>
    /// 封面大小（百分比，60~125：上限由条子高度反推，保证不会被裁 ✓）。**与字号完全独立** ✓（用户要求分开设置 ✓）
    /// 100% = 与两行歌词等高（即原来的对齐外观 ✓）→ 所以默认值不会改变现有观感 ✓
    /// </summary>
    public int ArtSizePercent { get; set; } = 100;

    public void Normalize()
    {
        Lines = Math.Clamp(Lines, 1, 2);
        WidthDip = Math.Clamp(WidthDip, 100, 900);   // 上限 600 → 800 → 900（真正的天花板由任务栏空档决定，见 bar.maxTextDip）
        TextColor = NormalizeColor(TextColor, "#FFFFFF");
        HighlightColor = NormalizeColor(HighlightColor, "#4CC2FF");
        NextLineColor = NormalizeColor(NextLineColor, "#8AFFFFFF");
        Alignment = Alignment switch
        {
            "center" => "center",
            "right" => "right",
            _ => "left",
        };
        FontScalePercent = Math.Clamp(FontScalePercent, 80, 150);
        ArtSizePercent = Math.Clamp(ArtSizePercent, 60, 125);
    }

    /// <summary>颜色串不合法就退回默认值（不因为一个手写错的值让歌词看不清）。</summary>
    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string text = value.Trim();
        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        string hex = text[1..];
        if (hex.Length is not (6 or 8))
        {
            return fallback;
        }

        foreach (char ch in hex)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return fallback;
            }
        }

        return text.ToUpperInvariant();
    }
}

/// <summary>
/// 切歌通知设置（与提示窗同一套落点取值）。
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>切歌时是否弹出通知。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>通知落点：topleft / topcenter / topright / bottomleft / bottomcenter / bottomright / aboveartwork。</summary>
    public string Position { get; set; } = "aboveArtwork";

    /// <summary>停留时长（毫秒）。参照实现默认 3 秒。</summary>
    public int Milliseconds { get; set; } = 3000;

    /// <summary>是否显示封面图。</summary>
    public bool ShowArtwork { get; set; } = true;

    public void Normalize()
    {
        Milliseconds = Math.Clamp(Milliseconds, 500, 20000);
        if (string.IsNullOrWhiteSpace(Position))
        {
            Position = "aboveArtwork";
        }
    }
}

/// <summary>界面外观设置（窗口层，与媒体条本身无关）。</summary>
public sealed class UiSettings
{
    /// <summary>设置窗口的高亮色跟随 Windows 主题强调色（壁纸引擎换壁纸自动提色时也会跟着变）。</summary>
    public bool FollowSystemAccent { get; set; } = true;

    public void Normalize()
    {
    }
}

/// <summary>
/// 媒体条自身的宽度策略（用户说的"安全区"）：
/// 手动指定宽度、或在任务栏空档不够时先压缩再考虑隐藏。
/// </summary>
public sealed class BarSettings
{
    /// <summary>宽度模式：auto = 按内容自适应；fixed = 固定宽度（手动避开任务栏图标）。</summary>
    public string WidthMode { get; set; } = "auto";

    /// <summary>固定模式下的整条宽度（DIP）。</summary>
    public int WidthDip { get; set; } = 520;

    /// <summary>任务栏空档放不下时，是否先逐步压缩到最小宽度再隐藏（默认开）。</summary>
    public bool ShrinkWhenTight { get; set; } = true;

    /// <summary>压缩下限，也是"可接受的最小宽度"（DIP）。</summary>
    public int MinimumWidthDip { get; set; } = 320;

    /// <summary>环形进度：绕在专辑图外圈的一圈进度。</summary>
    public bool RingProgress { get; set; }

    /// <summary>条状进度：原来的横向细条。</summary>
    public bool BarProgress { get; set; } = true;

    /// <summary>控制按钮：上一首 / 播放暂停 / 下一首。</summary>
    public bool ShowControls { get; set; } = true;

    /// <summary>环形进度上可以按住拖动跳转（仅显示环形进度时有效）。</summary>
    public bool RingSeek { get; set; } = true;

    /// <summary>
    /// 环形进度的已播颜色。`accent` = 跟随系统强调色（默认）；
    /// 其它值形如 `#RRGGBB` / `#AARRGGBB`，与歌词配色**共用同一套预设**。
    /// </summary>
    public string RingColor { get; set; } = "accent";

    /// <summary>环形进度"已播"部分的**不透明度**（0~100 %）。</summary>
    public int RingOpacity { get; set; } = 100;

    /// <summary>
    /// 环形进度的线条粗细（DIP，1~3：细/中/粗）。
    /// 1 DIP 在 150% 缩放下 ≈ 1.5 物理像素 ✓ 已经是"最细还能看见"的程度 ✓；
    /// 上限 6：实测 8 DIP 会把封面的圆角盖掉 ✗，6 是"再粗就吃掉封面"的临界 ✓。
    /// 轨道按它的 55% 画 ✓（保持"已播更突出"的层次 ✓）
    /// </summary>
    public int RingThicknessDip { get; set; } = 3;

    /// <summary>
    /// 媒体条**背景面板**的不透明度（0~100 %）。0 = 完全透明（只剩内容浮在任务栏上 ✓）。
    /// 只影响面板本身 ✓ 封面/文字/按钮始终实心 ✓ —— 参照 AFM 的观感 ✓
    /// </summary>
    public int BackgroundOpacity { get; set; } = 100;

    /// <summary>频谱：在歌词右侧显示一排频谱柱。</summary>
    public bool Spectrum { get; set; } = true;

    /// <summary>频谱柱数量（4~16）。</summary>
    public int SpectrumBars { get; set; } = 9;

    public void Normalize()
    {
        WidthMode = WidthMode == "fixed" ? "fixed" : "auto";
        WidthDip = Math.Clamp(WidthDip, 120, 800);
        MinimumWidthDip = Math.Clamp(MinimumWidthDip, 160, 800);
        if (MinimumWidthDip > WidthDip)
        {
            MinimumWidthDip = WidthDip;
        }
        if (!string.Equals(RingColor, "accent", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(RingColor) || !RingColor.StartsWith('#')))
        {
            RingColor = "accent";
        }
        RingOpacity = Math.Clamp(RingOpacity, 0, 100);
        RingThicknessDip = Math.Clamp(RingThicknessDip, 1, 3);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 100);
        SpectrumBars = Math.Clamp(SpectrumBars, 4, 16);
    }
}

public sealed class RyuBarSettings
{
    public int SchemaVersion { get; set; } = 1;

    public FadeSettings Fade { get; set; } = new();

    public ReaderSettings Readers { get; set; } = new();

    public InteractionSettings Interaction { get; set; } = new();

    public LyricsSettings Lyrics { get; set; } = new();

    public NotificationSettings Notification { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    /// <summary>媒体条宽度策略（"安全区"）。</summary>
    public BarSettings Bar { get; set; } = new();

    public void Normalize()
    {
        Fade ??= new FadeSettings();
        Fade.Normalize();
        Readers ??= new ReaderSettings();
        Readers.Normalize();
        Interaction ??= new InteractionSettings();
        Interaction.Normalize();
        Lyrics ??= new LyricsSettings();
        Lyrics.Normalize();
        Notification ??= new NotificationSettings();
        Notification.Normalize();
        Ui ??= new UiSettings();
        Ui.Normalize();
        Bar ??= new BarSettings();
        Bar.Normalize();
    }
}

/// <summary>
/// 设置文件读写：%LOCALAPPDATA%\RyuBar\settings.json。
/// 首次运行写一份带默认值的文件（方便直接打开改），改动会在下次轮询时被读入并生效，不需要重启。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private DateTime _lastWriteUtc;

    public RyuBarSettings Current { get; private set; } = new();

    public string FilePath { get; }

    /// <summary>文件被改动后读到新内容时触发（在调用 Load/PollForChanges 的线程上）。</summary>
    public event Action? Changed;

    public SettingsStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RyuBar",
            "settings.json");
    }

    public void Load()
    {
        RyuBarSettings settings;
        bool writeBack = false;
        DateTime writeUtc = DateTime.MinValue;

        try
        {
            if (File.Exists(FilePath))
            {
                writeUtc = File.GetLastWriteTimeUtc(FilePath);
                string json = File.ReadAllText(FilePath);
                settings = JsonSerializer.Deserialize<RyuBarSettings>(json, ReadOptions) ?? new RyuBarSettings();
                // 读进来之后**一律规范化写回**：这样后加的新键（如 interaction.tooltipPosition）会出现在文件里，
                // 用户才知道有这个可选项。只判"段在不在"是不够的 —— 段早就有了，缺的是段里的新键。
                writeBack = true;
                settings.Normalize();
            }
            else
            {
                settings = new RyuBarSettings();
                writeBack = true;
            }
        }
        catch
        {
            // 文件坏了就用默认值，并且重写一份，避免每次启动都读坏文件
            settings = new RyuBarSettings();
            writeBack = true;
        }

        lock (_gate)
        {
            Current = settings;
            _lastWriteUtc = writeUtc;
        }

        if (writeBack)
        {
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>轮询文件是否被外部改动（用户在编辑器里改了值），改了就读入并通知。</summary>
    public void PollForChanges()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }
            DateTime writeUtc = File.GetLastWriteTimeUtc(FilePath);
            lock (_gate)
            {
                // 别把"我们自己刚写回去"当成外部改动：写完之后 _lastWriteUtc 已经跟着更新了
                if (writeUtc <= _lastWriteUtc)
                {
                    return;
                }
            }
            Load();
        }
        catch
        {
        }
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            RyuBarSettings snapshot;
            lock (_gate)
            {
                snapshot = Current;
            }

            string json = JsonSerializer.Serialize(snapshot, WriteOptions);
            File.WriteAllText(FilePath, json);
            lock (_gate)
            {
                _lastWriteUtc = File.GetLastWriteTimeUtc(FilePath);
            }
        }
        catch
        {
        }
    }
}
