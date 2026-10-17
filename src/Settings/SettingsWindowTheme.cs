using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using RyuBar.Configuration;

namespace RyuBar.Settings;

/// <summary>
/// 正式设置窗口：**只用 WPF 自带控件 + 自带 Fluent 主题**（App.xaml 的 ThemeMode="System"），
/// 不引任何第三方 UI 库。库不给的四样东西自己画：
///   ① 卡片行（圆角面板 + 细描边）  ② 图标块（强调色底 + 字体图标）
///   ③ 胶囊开关（ToggleButton 模板）  ④ 数字步进（TextBox + 两个 RepeatButton）
///
/// 配色策略：**不猜主题的资源键名**（找不到会静默变透明），
/// 明暗由注册表判断、强调色用第一方 <see cref="SystemColors.AccentColor"/>，
/// 文字颜色则**完全不设**，交给主题 —— 副标题一律用 Opacity 表达层级（明暗两套都成立）。
/// </summary>
public sealed class SettingsWindowTheme : Window
{
    private readonly SettingsStore _store;
    private readonly StackPanel _pageHost = new();
    private bool _loading;

    // ---- 可收起的侧边栏（自己实现：ListBox 的高亮是主题模板画的方角，且没有收起形态）----
    private readonly Border _navPane = new();
    private readonly StackPanel _navList = new();
    private Button? _navToggle;

    /// <summary>联动更新器：每次 Apply 之后重跑（联动变灰 / 防呆）。切页时清空。</summary>
    private readonly List<Action> _linkage = new();

    /// <summary>「位置」分组里的实测空档/下限提示（放不下时变红告警）。</summary>
    private readonly TextBlock _spaceNote = new()
    {
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 8, 0, 4),
    };

    /// <summary>每秒刷新一次"实测空档 + 可调下限"（它们随当前曲目变化，只读一次会过期）。</summary>
    private System.Windows.Threading.DispatcherTimer? _liveTimer;

    /// <summary>「固定宽度」的动态下限（= 整条可行最小宽度，来自 bar 视图实测）。</summary>
    private int _fixedWidthMinDip;

    /// <summary>数值行里的输入框（按行标题存），用来动态改下限、把钳后的值写回去。</summary>
    private readonly Dictionary<string, TextBox> _numberBoxes = new();

    /// <summary>歌词页的"上限说明"文本（动态：每秒跟着主程序实测值刷新）。</summary>
    private TextBlock? _lyricsWidthNote;

    // 侧栏宽度动画（自己用定时器插值，见 SetNavExpanded）
    private System.Windows.Threading.DispatcherTimer? _navTimer;
    private DateTime _navAnimStart;
    private double _navFrom;
    private double _navTo;
    private double _insetFrom = 6;
    private double _insetTo = 6;
    private readonly List<(string Tag, string Label, RadioButton Item, TextBlock LabelBlock, StackPanel Wide, StackPanel Narrow)> _navEntries = new();
    private bool _navExpanded = true;
    private const double NavWidthExpanded = 188;
    private const double NavWidthCollapsed = 64;

    private static string _lastPage = "lyrics";

    private static readonly bool Dark = IsDarkTheme();

    // 主题无关的"对比层"：暗色下叠一点白、亮色下叠一点黑，深浅两套都成立
    private static readonly Color CardFill = Dark
        ? Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0x0B, 0x00, 0x00, 0x00);
    private static readonly Color CardStroke = Dark
        ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0x16, 0x00, 0x00, 0x00);
    private static readonly Color TrackOff = Dark
        ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0x3D, 0x00, 0x00, 0x00);

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily UiFont = new("Microsoft YaHei UI, Segoe UI");

    private static readonly string[] PositionOptions = { "条上方", "左上", "中上", "右上", "左下", "中下", "右下" };
    private static readonly string[] PositionValues = { "aboveArtwork", "topleft", "topcenter", "topright", "bottomleft", "bottomcenter", "bottomright" };
    private static readonly string[] WheelOptions = { "切歌", "换源", "不响应" };
    private static readonly string[] AlignValues = { "left", "center", "right" };
    private static readonly string[] WheelValues = { "previousNext", "switchSource", "none" };
    /// <summary>颜色预设：显示名字，存 16 进制值。（不能用 ValueTuple —— 它的属性叫 Item1/Item2，DisplayMemberPath="Name" 会显示空白）</summary>
    private sealed record ColorPreset(string Name, string Value);

    private static readonly ColorPreset[] ColorPresets =
    {
        new("白色", "#FFFFFF"),
        new("浅青", "#E6F7FF"),
        new("橙黄", "#FFB454"),
        new("天蓝", "#4CC2FF"),
        new("浅绿", "#7CE38B"),
        new("樱粉", "#FF7A9C"),
        new("淡紫", "#C79CFF"),
        new("半透明", "#8AFFFFFF"),
    };

    private static readonly (string Tag, string Glyph, string Label)[] Pages =
    {
        ("show", "\uE7C4", "显示与位置"),
        ("look", "\uE790", "外观"),
        ("lyrics", "\uE8D6", "歌词"),
        ("notify", "\uE7E7", "切歌通知"),
        ("input", "\uE7C9", "交互"),
        ("app", "\uE713", "应用"),
    };

    public SettingsWindowTheme(SettingsStore store)
    {
        _store = store;

        Title = "RyuBar 设置";
        Width = 980;
        Height = 660;
        MinWidth = 860;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = UiFont;
        FontSize = 13.5;
        ShowInTaskbar = true;

        Content = BuildShell();
        SelectPage(_lastPage);

        // 诊断：RYUBAR_NAV_START=0 直接以"收起态"启动（汉堡已隐藏，方便用日志排查收起后的渲染问题）
        if (Environment.GetEnvironmentVariable("RYUBAR_NAV_START") == "0")
        {
            SetNavExpanded(false, animate: false);
        }

        Loaded += (_, _) =>
        {
            LogNavState("初次布局");

            // "第一次点击没反应"：窗口没拿到前台焦点时，第一次点击会被系统用于激活窗口。
            // 这里在加载完成后主动抢一次前台 + 焦点，并把结果写进日志（便于确认是否真的抢到）。
            try
            {
                Activate();
                SetForegroundWindow(new WindowInteropHelper(this).Handle);
                Focus();
                Diagnostics.Log.Write("nav", $"抢焦点结果: IsActive={IsActive} IsFocused={IsKeyboardFocused} 句柄={new WindowInteropHelper(this).Handle}");
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("nav", "抢焦点失败", ex);
            }
        };

        // 诊断：把前几次点击的命中元素记下来（用于定位"第一次点击没反应"）
        int probes = 0;
        PreviewMouseDown += (_, e) =>
        {
            if (probes++ < 4)
            {
                System.Windows.IInputElement? hit = e.OriginalSource as System.Windows.IInputElement;
                Diagnostics.Log.Write("nav",
                    $"点击#{probes} at ({e.GetPosition(this).X:0},{e.GetPosition(this).Y:0}) 命中={hit?.GetType().Name ?? "null"} 窗口活动={IsActive}");
            }
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>把侧栏的真实布局数值写日志 —— 排查"收起后项不渲染"时靠它，比猜可靠。</summary>
    private void LogNavState(string when)
    {
        try
        {
            Diagnostics.Log.Write("nav", $"[{when}] 展开={_navExpanded} 面板Width={_navPane.Width} ActualWidth={_navPane.ActualWidth:0.#} 项数={_navEntries.Count}");
            foreach ((string tag, _, RadioButton item, _, StackPanel wide, StackPanel narrow) in _navEntries)
            {
                FrameworkElement content = (FrameworkElement)item.Content;
                int childCount = content is Panel panel ? panel.Children.Count : -1;
                Diagnostics.Log.Write("nav",
                    $"    {tag}: item={item.ActualWidth:0.#}x{item.ActualHeight:0.#} vis={item.Visibility} " +
                    $"contentIsWide={ReferenceEquals(content, wide)} content={content.ActualWidth:0.#}x{content.ActualHeight:0.#} vis={content.Visibility} " +
                    $"childCount={childCount}");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Write("nav", "布局诊断输出失败", ex);
        }
    }

    // ---------------------------------------------------------------- 外壳

    private UIElement BuildShell()
    {
        Grid root = new();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- 左：可收起的侧栏（自绘圆角项）----
        _navPane.Width = NavWidthExpanded;
        _navPane.Margin = new Thickness(8, 8, 4, 8);
        // 让看不见但仍占位的文字被侧栏裁掉，而不是溢出到内容区
        _navPane.ClipToBounds = true;

        _navList.Margin = new Thickness(0, 4, 0, 0);
        _navPane.Child = new StackPanel
        {
            Children =
            {
                BuildHamburger(),
                _navList,
            },
        };
        Grid.SetColumn(_navPane, 0);
        root.Children.Add(_navPane);

        foreach ((string tag, string glyph, string label) in Pages)
        {
            _navList.Children.Add(BuildNavItem(tag, glyph, label));
        }

        // ---- 右：滚动内容 ----
        ScrollViewer scroll = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(14, 8, 28, 28),   // 左边距收窄：别和侧栏拉出很空的缝
            Content = _pageHost,
        };
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);

        return root;
    }

    private UIElement BuildHamburger()
    {
        // 参考图那种：**无边框纯图标按钮** —— 常态没有底/描边，只在悬停时出现圆角底。
        // （主题的 Button 样式默认带一层底，所以要显式清掉 Background/BorderThickness）
        _navToggle = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE700",
                FontFamily = IconFont,
                FontSize = 16,
            },
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 4, 6, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Template = FlatIconButtonTemplate(),
            ToolTip = "收起 / 展开导航",
        };
        _navToggle.Click += (_, _) => SetNavExpanded(!_navExpanded, animate: true);
        return _navToggle;
    }

    /// <summary>导航项：RadioButton + 自写模板（圆角 6 + 选中强调色竖条 + 悬停反馈）。</summary>
    private UIElement BuildNavItem(string tag, string glyph, string label)
    {
        // **只有一套内容**：图标 + 文字，永远左对齐。
        // 图标因此在整个收起/展开过程中**坐标不变**，只有侧栏右边缘在动 ——
        // 之前用两套内容（展开=左对齐 / 收起=居中）在动画结束时切换，造成图标"最后一刻横跳"
        // 和展开时"先跑到中间再瞬移到最左"（用户实测描述得非常准）。
        TextBlock icon = new()
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 15,
            Width = 20,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

            TextBlock labelBlock = new()
            {
                Text = label,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                // **永远参与布局**（只调透明度）：设成 Collapsed 会让行变矮 → 整列图标往上收（用户实测指出）
                Visibility = Visibility.Visible,
                Opacity = _navExpanded ? 1 : 0,
            };

        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(11, 8, 11, 8),
            MinHeight = 18,   // 固定行高：文字淡出时不改变行高（否则整列图标会往上收）
            Children = { icon, labelBlock },
        };

        RadioButton item = new()
        {
            Content = content,
            Tag = tag,
            GroupName = "RyuBarNav",
            Template = NavItemTemplate(),
            Margin = new Thickness(6, 2, 6, 2),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = label,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
        };
        item.Checked += (_, _) =>
        {
            if (!_loading)
            {
                SelectPage(tag);
            }
        };

        _navEntries.Add((tag, label, item, labelBlock, content, content));
        return item;
    }

    private static ControlTemplate NavItemTemplate()
    {
        Color accent = SystemColors.AccentColor;

        FrameworkElementFactory bg = new(typeof(Border), "Bg");
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        FrameworkElementFactory bar = new(typeof(Border), "Bar");
        bar.SetValue(FrameworkElement.WidthProperty, 3.0);
        bar.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        bar.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 7, 0, 7));
        bar.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        bar.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xFF, accent.R, accent.G, accent.B)));
        bar.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

        FrameworkElementFactory grid = new(typeof(Grid));
        grid.AppendChild(bg);
        grid.AppendChild(bar);
        FrameworkElementFactory presenter = new(typeof(ContentPresenter));
        // 内边距由"内容"自己带（见 BuildNavItem），模板这里留 0。
        // **对齐交给内容自己**：模板若设 Center，各行按各自文字宽度居中 → 图标不在同一条竖线上（实测被用户一眼看出）
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(presenter);

        ControlTemplate template = new(typeof(RadioButton)) { VisualTree = grid };

        Trigger hover = new() { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0x00, 0x00, 0x00)), "Bg"));
        template.Triggers.Add(hover);

        Trigger selected = new() { Property = ToggleButton.IsCheckedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Dark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00)), "Bg"));
        selected.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Bar"));
        template.Triggers.Add(selected);

        return template;
    }

    /// <summary>
    /// 收起 / 展开侧栏。
    /// **宽度动画用 DispatcherTimer 自己插值** ✗ 不用 DoubleAnimation ——
    /// 因为侧栏放在 `Auto` 列里时 `BeginAnimation(WidthProperty, ...)` 实测**没有中间帧**（逐帧抓图验证：一帧跳变），
    /// 自己每 16ms 设一次 Width 就完全可控、也能被逐帧验证。
    /// 内容在动画结束后统一重建（避免"先按收起态居中→图标跳一下"）。
    /// </summary>
    private void SetNavExpanded(bool expanded, bool animate)
    {
        bool wasExpanded = _navExpanded;
        _navExpanded = expanded;
        double from = _navPane.ActualWidth > 0 ? _navPane.ActualWidth
            : (wasExpanded ? NavWidthExpanded : NavWidthCollapsed);
        double target = expanded ? NavWidthExpanded : NavWidthCollapsed;

        if (!animate || Math.Abs(from - target) < 1)
        {
            _navTimer?.Stop();
            _navPane.Width = target;
            ApplyLabelVisibility(expanded, animate: false);
            ApplyCollapsedHighlightInset();
            LogNavState(expanded ? "切到展开" : "切到收起");
            return;
        }

        ApplyLabelVisibility(expanded, animate: true);   // 文字淡入/淡出，**图标坐标始终不变**

        // 高亮的右边距也**跟着动画一起插值**（收起 6→18 / 展开 18→6）——
        // 之前只在动画结束时改 ✗ 造成"高亮一路跟随缩小，最后一刻突然改变右宽度"（用户实测指出）。
        // 只动**右边距**，左边距与图标坐标保持不变。
        _insetFrom = wasExpanded ? 6 : 18;
        _insetTo = expanded ? 6 : 18;

        _navFrom = from;
        _navTo = target;
        _navAnimStart = DateTime.Now;
        _navTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _navTimer.Tick -= OnNavAnimTick;
        _navTimer.Tick += OnNavAnimTick;
        _navTimer.Start();
    }

    private void OnNavAnimTick(object? sender, EventArgs args)
    {
        const double durationMs = 320;
        double t = (DateTime.Now - _navAnimStart).TotalMilliseconds / durationMs;

        if (t >= 1)
        {
            _navTimer!.Stop();
            _navPane.Width = _navTo;
            ApplyCollapsedHighlightInset();   // 到位后统一成最终值（插值已基本相同，这里只是收口）
            LogNavState(_navExpanded ? "动画结束→展开" : "动画结束→收起");
            return;
        }

        double eased = 1 - Math.Pow(1 - t, 3);   // CubicEaseOut
        _navPane.Width = _navFrom + ((_navTo - _navFrom) * eased);

        // 高亮右边距同步插值：视觉上高亮与侧栏一起收窄，没有末刻跳变
        double inset = _insetFrom + ((_insetTo - _insetFrom) * eased);
        foreach ((_, _, RadioButton item, _, _, _) in _navEntries)
        {
            item.Margin = new Thickness(6, 2, inset, 2);
        }

        if (Environment.GetEnvironmentVariable("RYUBAR_NAV_TRACE") == "1")
        {
            Diagnostics.Log.Write("nav", $"  t={t * 100:0}%  宽={_navPane.Width:0.#}  右留白={inset:0.#}");
        }
    }

    /// <summary>
    /// 收起 / 展开时**只调文字的透明度**（文字始终参与布局，行高不变 → 图标不会上下移动）；
    /// 看不见的文字由侧栏的 ClipToBounds 裁掉，不会溢出到内容区。
    /// 收起结束后再把导航项**右侧留白加大**，让选中高亮在窄栏里是个接近方形的块（用户反馈右侧太宽不协调）；
    /// 展开开始时立刻恢复 —— 只动右边距，**左边距与图标坐标不变**。
    /// </summary>
    private void ApplyLabelVisibility(bool expanded, bool animate)
    {
        foreach ((_, string label, RadioButton item, TextBlock labelBlock, _, _) in _navEntries)
        {
            ToolTipService.SetToolTip(item, expanded ? null : label);

            if (animate)
            {
                labelBlock.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(expanded ? 1 : 0, TimeSpan.FromMilliseconds(160))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    });
            }
            else
            {
                labelBlock.BeginAnimation(OpacityProperty, null);
                labelBlock.Opacity = expanded ? 1 : 0;
            }

            // 展开时立刻恢复右侧边距；收起时留到最后（窄栏成型后再收窄高亮）
            if (expanded)
            {
                item.Margin = new Thickness(6, 2, 6, 2);
            }
        }
    }

    /// <summary>收起动画结束后：把高亮收成接近方形的块（右留白加大，左边距不动）。</summary>
    private void ApplyCollapsedHighlightInset()
    {
        if (_navExpanded)
        {
            return;
        }

        foreach ((_, _, RadioButton item, _, _, _) in _navEntries)
        {
            item.Margin = new Thickness(6, 2, 18, 2);
        }
    }

    private string CurrentTag() => _lastPage;

    private void SelectPage(string tag)
    {
        _lastPage = tag;

        foreach ((string entryTag, _, RadioButton item, _, _, _) in _navEntries)
        {
            bool isCurrent = entryTag == tag;
            if (item.IsChecked != isCurrent)
            {
                item.IsChecked = isCurrent;
            }
        }

        _pageHost.Children.Clear();
        _loading = true;

        // **单页构建失败不能冻住整个导航** ✗
        // 教训：一次"元素已属于另一个父级"的异常发生在建页过程中 ✓ →
        // 之后每次点导航都在同一行炸 ✓ → 用户看到的是"点了完全没反应"（排查成本极高 ✓）。
        // 现在：哪一页炸了就只显示那一页的错误提示 ✓ 其它页照常可用 ✓
        try
        {
            switch (tag)
            {
                case "show":
                    BuildShowPage();
                    break;
                case "look":
                    BuildLookPage();
                    break;
                case "notify":
                    BuildNotifyPage();
                    break;
                case "input":
                    BuildInputPage();
                    break;
                case "app":
                    BuildAppPage();
                    break;
                default:
                    BuildLyricsPage();
                    break;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Write("nav", $"建页失败 [{tag}]：{ex.GetType().Name}: {ex.Message}");
            _pageHost.Children.Clear();
            _pageHost.Children.Add(new TextBlock
            {
                Text = $"这一页构建失败：{ex.Message}\n（其它页面仍可正常使用；详情见日志）",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 12, 0, 0),
            });
        }
        _loading = false;
    }

    // ---------------------------------------------------------------- 行构件（自己画的四样）

    /// <summary>
    /// 扁平图标按钮模板：常态无底无描边，**悬停/按下有自己的反馈**。
    /// 为什么不在主题模板上设 Background=Transparent ✗ ——
    /// 那会覆盖模板里 hover/pressed 的底色，控件就永远没有反馈（用户反馈"看不出有没有点到"）。
    /// </summary>
    private static ControlTemplate FlatIconButtonTemplate()
    {
        FrameworkElementFactory bg = new(typeof(Border), "Bg");
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        FrameworkElementFactory presenter = new(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        FrameworkElementFactory grid = new(typeof(Grid));
        grid.AppendChild(bg);
        grid.AppendChild(presenter);

        ControlTemplate template = new(typeof(ButtonBase)) { VisualTree = grid };

        Trigger hover = new() { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0E, 0x00, 0x00, 0x00)), "Bg"));
        template.Triggers.Add(hover);

        Trigger pressed = new() { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Dark ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00)), "Bg"));
        template.Triggers.Add(pressed);

        return template;
    }

    private void Header(string title, string subtitle)
    {
        _pageHost.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            Margin = new Thickness(0, 4, 0, 2),
        });
        _pageHost.Children.Add(new TextBlock
        {
            Text = subtitle,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });
    }

    private void Group(string text) => _pageHost.Children.Add(new TextBlock
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Opacity = 0.75,
        Margin = new Thickness(0, 16, 0, 8),
    });

    private void Note(string text) => _pageHost.Children.Add(new TextBlock
    {
        Text = text,
        Opacity = 0.6,
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 6),
    });

    /// <summary>① 卡片行：图标块 + 标题 + 说明 + 右侧控件。返回行本身，便于做"联动变灰"。</summary>
    private Border Card(string title, string description, string glyph, FrameworkElement? control)
    {
        Border card = new()
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(CardFill),
            BorderBrush = new SolidColorBrush(CardStroke),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 3, 0, 3),
        };

        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(Tile(glyph));

        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Opacity = 0.62,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (control != null)
        {
            // 右侧控件列**定宽 + 右对齐**，并**清掉控件自带的外边距** ——
            // 主题的 ComboBox/ToggleButton 样式各带不同外边距，实测右边缘能差 10~51px（用户指出"不对齐很突兀"）。
            // 清零后所有"可见的框"右边缘落在同一条竖线上。
            Grid slot = new()
            {
                Width = 210,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            control.HorizontalAlignment = HorizontalAlignment.Right;
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(0);
            slot.Children.Add(control);
            Grid.SetColumn(slot, 2);
            grid.Children.Add(slot);
        }

        card.Child = grid;
        _pageHost.Children.Add(card);
        return card;
    }

    /// <summary>
    /// 联动变灰 + 防呆：条件不成立时这一行**整行禁用**（IsEnabled=false → 点不动 ✓）
    /// 并且降低不透明度（看得见但明显是灰的 ✓）。行不会消失，所以布局不跳 ✓。
    /// 条件成立与否在每次 Apply 之后重跑 ✓（切页时清空 ✓）。
    /// </summary>
    private void LinkRow(Border row, Func<bool> enabled)
    {
        void Update()
        {
            bool on = enabled();
            row.IsEnabled = on;
            row.Opacity = on ? 1.0 : 0.45;
        }

        _linkage.Add(Update);
        Update();
    }

    /// <summary>② 图标块：强调色底 + 字体图标（强调色取第一方 SystemColors.AccentColor）。</summary>
    private static Border Tile(string glyph)
    {
        Color accent = SystemColors.AccentColor;
        return new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, accent.R, accent.G, accent.B)),
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = 15,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private Border Toggle(string title, string description, string glyph, Func<bool> get, Action<bool> set)
    {
        bool on = get();
        Color accent = SystemColors.AccentColor;

        // 开关的视觉直接做在 Content 上（不是模板里）→ 代码能抓住两个部件，动画才好写
        Border track = new()
        {
            Width = 40,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(on ? accent : TrackOff),
        };
        Ellipse knob = new()
        {
            Width = 12,
            Height = 12,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            RenderTransform = new TranslateTransform(on ? 20 : 0, 0),
        };
        Grid visual = new() { Width = 40, Height = 20 };
        visual.Children.Add(track);
        visual.Children.Add(knob);

        ToggleButton toggle = new()
        {
            Content = visual,
            Template = BareToggleTemplate(),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        toggle.Checked += (_, _) => { AnimatePill(track, knob, true, accent); Apply(() => set(true)); };
        toggle.Unchecked += (_, _) => { AnimatePill(track, knob, false, accent); Apply(() => set(false)); };

        // 返回整行（Border）→ 调用方可以 LinkRow 做"联动变灰" ✓（原来是 void：开关行没法变灰 ✗）
        return Card(title, description, glyph, toggle);
    }

    /// <summary>胶囊开关动画：圆点滑动 + 轨道颜色渐变（用户反馈"瞬间切换很突兀"）。</summary>
    private static void AnimatePill(Border track, Ellipse knob, bool on, Color accent)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(150);
        CubicEase ease = new() { EasingMode = EasingMode.EaseOut };

        if (knob.RenderTransform is TranslateTransform offset)
        {
            offset.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(on ? 20 : 0, duration) { EasingFunction = ease });
        }

        if (track.Background is SolidColorBrush brush)
        {
            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(on ? accent : TrackOff, duration) { EasingFunction = ease });
        }
    }

    /// <summary>极简模板：只呈现 Content（去掉主题按钮那层底/描边）。</summary>
    private static ControlTemplate BareToggleTemplate()
    {
        FrameworkElementFactory presenter = new(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new ControlTemplate(typeof(ToggleButton)) { VisualTree = presenter };
    }

    private void Choice(string title, string description, string glyph, string[] options, Func<int> get, Action<int> set)
    {
        ComboBox combo = new()
        {
            Width = 168,   // 固定并与其它行一致：不用自适应，避免各行左边缘参差
            ItemsSource = options,
            SelectedIndex = Math.Clamp(get(), 0, options.Length - 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 兜底：文案再长也不会"看不全"，悬停显示完整选项
        combo.SelectionChanged += (_, _) =>
        {
            if (!_loading && combo.SelectedIndex >= 0)
            {
                Apply(() => set(combo.SelectedIndex));
            }
            ToolTipService.SetToolTip(combo, combo.SelectedItem?.ToString());
        };
        ToolTipService.SetToolTip(combo, combo.SelectedItem?.ToString());
        Card(title, description, glyph, combo);
    }

    /// <summary>④ 数字步进：TextBox + 两个 RepeatButton（自带主题没有 NumberBox）。</summary>
    private Border Number(string title, string description, string glyph, int min, int max, int step,
        Func<int> get, Action<int> set, string suffix, Func<int>? maxProvider = null)
    {
        TextBox box = new()
        {
            Text = get().ToString(),
            Height = 32,
            TextAlignment = TextAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0, 4, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };
        _numberBoxes[title] = box;

        RepeatButton up = StepperButton("\uE70E");
        RepeatButton down = StepperButton("\uE70D");
        up.Margin = new Thickness(0, 0, 0, 1);
        down.Margin = new Thickness(0, 1, 0, 0);

        // 参考 WinUI NumberBox：**箭头在输入框内部右侧**（一个圆角框，右内侧上下两个箭头）
        // 每个箭头占一半高度（各留 1px 缝），点上去高亮覆盖**整个半格**而不只是图标
        Grid arrows = new()
        {
            Width = 30,
            Height = 32,
            Margin = new Thickness(0, 0, 1, 0),
        };
        arrows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        arrows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(up, 0);
        Grid.SetRow(down, 1);
        arrows.Children.Add(up);
        arrows.Children.Add(down);

        // 单位放进**框内**（像 Win11 的 NumberBox）：这样"可见的框"才是该行最右的元素，
        // 右边缘才能与其它行的框对齐（之前单位文字挂在框外 ✗ 抢了对齐基准位 ✗）
        TextBlock unit = new()
        {
            Text = suffix,
            Opacity = 0.55,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Visibility = suffix.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        };

        Grid inner = new();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.Children.Add(box);
        Grid.SetColumn(unit, 1);
        inner.Children.Add(unit);
        Grid.SetColumn(arrows, 2);
        inner.Children.Add(arrows);

        Border shell = new()
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(CardStroke),
            Background = new SolidColorBrush(Dark
                ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x0A, 0x00, 0x00, 0x00)),
            Width = 160,
            Height = 34,
            Child = inner,
        };

        int Value() => int.TryParse(box.Text, out int v) ? v : get();

        // 动态上限：有的值的上限随环境变化（"歌词自定义宽度"的上限 = 任务栏实测出来的最大值）
        // → 由调用方给一个 provider ✓ 输入/步进/回车三处都按它钳制 ✓
        int Max() => maxProvider is null ? max : Math.Clamp(maxProvider(), min, max);

        void Commit(int delta)
        {
            int next = Math.Clamp(Value() + delta, min, Max());
            box.Text = next.ToString();
            Apply(() => set(next));
        }

        up.Click += (_, _) => Commit(step);
        down.Click += (_, _) => Commit(-step);
        box.LostFocus += (_, _) =>
        {
            int next = Math.Clamp(Value(), min, Max());
            box.Text = next.ToString();
            Apply(() => set(next));
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                int next = Math.Clamp(Value(), min, Max());
                box.Text = next.ToString();
                Apply(() => set(next));
            }
        };

        StackPanel stepper = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        stepper.Children.Add(shell);
        return Card(title, description, glyph, stepper);
    }

    private static RepeatButton StepperButton(string glyph) => new()
    {
        Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 8 },
        Margin = new Thickness(0),
        Padding = new Thickness(0),
        Template = FlatIconButtonTemplate(),   // 悬停/按下自带高亮
        // **必须拉伸**：不然按钮缩成图标那么大，高亮也只剩图标那一小圈（用户实测指出）
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Focusable = false,
    };

    /// <summary>色块选颜色：显示**名字**（不显示 16 进制）+ 右侧实时色块。</summary>
    private void ColorRow(string title, string description, Func<string> get, Action<string> set)
    {
        Border swatch = new()
        {
            Width = 26,
            Height = 22,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(CardStroke),
            Background = ParseBrush(get()),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        ComboBox combo = new()
        {
            Width = 112,
            ItemsSource = ColorPresets,
            DisplayMemberPath = "Name",
            SelectedValuePath = "Value",
            VerticalAlignment = VerticalAlignment.Center,
        };
        string current = get();
        int index = Array.FindIndex(ColorPresets, p => string.Equals(p.Value, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index >= 0 ? index : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (!_loading && combo.SelectedItem is ColorPreset picked)
            {
                Apply(() => set(picked.Value));
                swatch.Background = ParseBrush(picked.Value);
            }
        };

        StackPanel host = new() { Orientation = Orientation.Horizontal };
        host.Children.Add(combo);
        host.Children.Add(swatch);
        Card(title, description, "\uE790", host);
    }

    /// <summary>
    /// 环形进度的颜色行：预设**复用歌词那一套**（用户要求 ✓）+ 一个「跟随主题色」。
    /// 默认「跟随主题色」= 保持原行为 ✓
    /// </summary>
    private void RingColorRow()
    {
        List<ColorPreset> items = new() { new ColorPreset("跟随主题色", "accent") };
        items.AddRange(ColorPresets);

        Border swatch = new()
        {
            Width = 26,
            Height = 22,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(CardStroke),
            Background = ParseBrush(CurrentRingColorHex()),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        ComboBox combo = new()
        {
            Width = 168,
            ItemsSource = items,
            DisplayMemberPath = "Name",
            SelectedValuePath = "Value",
            VerticalAlignment = VerticalAlignment.Center,
        };
        int index = items.FindIndex(p => string.Equals(p.Value, _store.Current.Bar.RingColor, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index >= 0 ? index : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (!_loading && combo.SelectedItem is ColorPreset picked)
            {
                Apply(() => _store.Current.Bar.RingColor = picked.Value);
                swatch.Background = ParseBrush(CurrentRingColorHex());
            }
        };

        StackPanel host = new() { Orientation = Orientation.Horizontal };
        host.Children.Add(combo);
        host.Children.Add(swatch);   // 色块只能有一个父级：直接挂在 host 上 ✓（早先我把它同时挂到两个容器 ✗ → 抛异常 → 整个导航失效）
        Card("封面进度颜色", "环形进度“已播”部分的颜色。预设与歌词配色一致；“跟随主题色”跟随系统强调色。", "\uE790", host);
    }

    /// <summary>
    /// 拖动条行（自己画的第 5 种行构件）：圆角轨道 + 强调色已选段 + 圆形滑块 + 右侧数值。
    /// 交互：**按下即定位** ✓ 拖动实时更新视觉 ✓ **松手才写盘** ✓
    /// （拖动过程中每帧写设置会连带主程序反复重载，实测成本很高 ✓ 所以只在松手时落盘 ✓）
    /// </summary>
    private void SliderRow(string title, string description, string glyph,
        int min, int max, int step, Func<int> get, Action<int> set, string suffix)
    {
        const double TrackWidth = 126;
        const double TrackHeight = 4;
        const double ThumbSize = 13;
        const double HostHeight = 22;

        Canvas canvas = new()
        {
            Width = TrackWidth,
            Height = HostHeight,
            Background = Brushes.Transparent,   // 透明也要能命中，否则点不到轨道空白处
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Border track = new()
        {
            Width = TrackWidth,
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            Background = new SolidColorBrush(CardStroke),
        };
        Border fill = new()
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            Background = new SolidColorBrush(SystemColors.AccentColor),
        };
        Ellipse thumb = new()
        {
            Width = ThumbSize,
            Height = ThumbSize,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(CardStroke),
            StrokeThickness = 1,
        };

        TextBlock valueText = new()
        {
            Text = get() + " " + suffix,
            Width = 46,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Opacity = 0.75,
            FontSize = 13,
        };

        Canvas.SetLeft(track, 0);
        Canvas.SetTop(track, (HostHeight - TrackHeight) / 2);
        Canvas.SetTop(fill, (HostHeight - TrackHeight) / 2);
        Canvas.SetTop(thumb, (HostHeight - ThumbSize) / 2);
        canvas.Children.Add(track);
        canvas.Children.Add(fill);
        canvas.Children.Add(thumb);

        double Ratio(int value) => Math.Clamp((value - min) / (double)(max - min), 0, 1);

        void Render(double ratio, bool write)
        {
            int value = (int)Math.Round(min + (ratio * (max - min)));
            if (step > 0)
            {
                value = (int)Math.Round((double)value / step) * step;
            }
            value = Math.Clamp(value, min, max);
            ratio = Ratio(value);

            fill.Width = Math.Max(0, ratio * TrackWidth);
            Canvas.SetLeft(thumb, (ratio * TrackWidth) - (ThumbSize / 2));
            valueText.Text = value + " " + suffix;

            if (write)
            {
                Apply(() => set(value));
            }
        }

        void FromPoint(Point p, bool write)
        {
            Render(Math.Clamp(p.X / TrackWidth, 0, 1), write);
        }

        bool dragging = false;
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
            canvas.CaptureMouse();
            Point p = e.GetPosition(canvas);
            Diagnostics.Log.Write("slider", $"[{title}] 按下 x={p.X:0.#} y={p.Y:0.#} 轨道宽={TrackWidth}");
            FromPoint(p, write: false);
            e.Handled = true;
        };
        canvas.MouseMove += (_, e) =>
        {
            if (dragging)
            {
                FromPoint(e.GetPosition(canvas), write: false);
            }
        };
        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (dragging)
            {
                dragging = false;
                canvas.ReleaseMouseCapture();
                Point p = e.GetPosition(canvas);
                Diagnostics.Log.Write("slider", $"[{title}] 松手 x={p.X:0.#} → 落盘");
                FromPoint(p, write: true);   // 松手才落盘 ✓
            }
        };

        Render(Ratio(Math.Clamp(get(), min, max)), write: false);

        StackPanel host = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(canvas);
        host.Children.Add(valueText);
        Card(title, description, glyph, host);
    }

    /// <summary>环形进度已播部分的不透明度（0~100 %）：和颜色搭配用，不必依赖“半透明”预设。</summary>
    private void RingOpacityRow()
    {
        SliderRow("封面进度不透明度", "环形进度“已播”部分的不透明度；0 = 完全透明，100 = 完全不透明。", "\uE8D2",
            0, 100, 5,
            () => _store.Current.Bar.RingOpacity,
            v => _store.Current.Bar.RingOpacity = Math.Clamp(v, 0, 100),
            "%");
    }

    /// <summary>
    /// 媒体条背景面板的不透明度（0~100 %，拖动条）。
    /// **只影响面板** ✓ 封面/歌词/按钮始终实心 ✓ —— 参照 AFM 的观感 ✓
    /// 0% 时面板完全消失，只剩内容浮在任务栏上 ✓
    /// </summary>
    private void BackgroundOpacityRow()
    {
        SliderRow("条子背景不透明度", "媒体条背景面板的不透明度；0 = 面板完全透明（只剩封面、文字与按钮）。", "\uE8D2",
            0, 100, 5,
            () => _store.Current.Bar.BackgroundOpacity,
            v => _store.Current.Bar.BackgroundOpacity = Math.Clamp(v, 0, 100),
            "%");
    }

    /// <summary>
    /// 环形进度线条粗细：**三个预设**（细 1 / 中 2 / 粗 3 DIP）。
    /// 1 DIP 在 150% 缩放下 ≈ 1.5 物理像素 ✓（最细还能看见 ✓）
    /// 轨道自动取该值的 55% ✓ 保持"已播更突出"的层次 ✓
    /// </summary>
    private void RingThicknessRow()
    {
        Choice("封面进度线条粗细", "环形进度的线条粗细；轨道会自动比它细一些。单位 DIP（150% 缩放下 1 DIP ≈ 1.5 像素）。", "\uE8D2",
            new[] { "细", "中", "粗" },
            () => Math.Clamp(_store.Current.Bar.RingThicknessDip, 1, 3) - 1,
            i => _store.Current.Bar.RingThicknessDip = Math.Clamp(i + 1, 1, 3));
    }

    /// <summary>当前环颜色（`accent` 时取系统强调色）的十六进制串，用于色块显示。</summary>
    private string CurrentRingColorHex()
    {
        string value = _store.Current.Bar.RingColor;
        return string.Equals(value, "accent", StringComparison.OrdinalIgnoreCase)
            ? $"#{SystemColors.AccentColor.R:X2}{SystemColors.AccentColor.G:X2}{SystemColors.AccentColor.B:X2}"
            : value;
    }

    private void ActionRow(string title, string description, string glyph, string buttonText, Action action)
    {
        Button button = new() { Content = buttonText, Padding = new Thickness(14, 5, 14, 5), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => action();
        Card(title, description, glyph, button);
    }

    private static Brush ParseBrush(string value)
    {
        try
        {
            string text = value.Trim().TrimStart('#');
            uint raw = Convert.ToUInt32(text, 16);
            Color color = text.Length == 8
                ? Color.FromArgb((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw)
                : Color.FromArgb(0xFF, (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
            return new SolidColorBrush(color);
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    /// <summary>
    /// 改一项 → 归一化 → 写盘（主程序的文件监听会即时生效），
    /// 然后**重跑联动**（联动变灰 / 防呆）—— 这一步以前漏了，导致"切成固定后固定宽度还是灰的"。
    /// </summary>
    private void Apply(Action change)
    {
        try
        {
            change();
            _store.Current.Fade.Normalize();
            _store.Current.Lyrics.Normalize();
            _store.Current.Notification.Normalize();
            _store.Current.Interaction.Normalize();
            _store.Current.Bar.Normalize();
            _store.Save();
        }
        catch
        {
            // 写失败不打断操作
        }

        foreach (Action update in _linkage)
        {
            try
            {
                update();
            }
            catch
            {
                // 单个联动失败不影响其它
            }
        }
    }

    private static bool IsDarkTheme()
    {
        try
        {
            object? value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int i && i == 0;
        }
        catch
        {
            return true;
        }
    }

    // ---------------------------------------------------------------- 六个页面

    private void BuildShowPage()
    {
        var fade = _store.Current.Fade;
        Header("显示与位置", "媒体条什么时候出现、怎么出现。");
        Group("出现与收起");
        Toggle("显示时淡入", "媒体条出现时淡入，而不是硬切。", "\uE7C4", () => fade.FadeInEnabled, v => fade.FadeInEnabled = v);
        Number("淡入时长", "淡入用多久。", "\uE916", 50, 800, 50, () => fade.FadeInMilliseconds, v => fade.FadeInMilliseconds = v, "毫秒");
        Number("淡入延后", "出现后等多久才开始淡入。", "\uE916", 0, 1000, 50, () => fade.FadeInDelayMilliseconds, v => fade.FadeInDelayMilliseconds = v, "毫秒");
        Toggle("隐藏时淡出", "媒体条收起时淡出。", "\uE7C4", () => fade.FadeOutEnabled, v => fade.FadeOutEnabled = v);
        Number("淡出时长", "淡出用多久。", "\uE916", 50, 800, 50, () => fade.FadeOutMilliseconds, v => fade.FadeOutMilliseconds = v, "毫秒");
        Group("位置");
        Choice("条子宽度", "自动 = 按内容自适应；固定 = 手动指定，用来避开任务栏图标。", "\uE7C4",
            new[] { "自动", "固定" },
            () => _store.Current.Bar.WidthMode == "fixed" ? 1 : 0,
            i => _store.Current.Bar.WidthMode = i == 1 ? "fixed" : "auto");

        // 防呆：固定宽度必须 ≥ 最小宽度（改哪个都把另一个钳到合法范围 ✓ 输入框里会显示钳后的值 ✓）
        Border widthRow = Number("固定宽度", "整条媒体条的宽度。仅在“条子宽度”选择“固定”时生效。", "\uE8D2", 120, 800, 20,
            () => _store.Current.Bar.WidthDip,
            v =>
            {
                int floor = _store.Current.Bar.MinimumWidthDip;
                _store.Current.Bar.WidthDip = (int)Math.Max(v, floor);
            }, "DIP");
        LinkRow(widthRow, () => _store.Current.Bar.WidthMode == "fixed");

        Toggle("空档不够时压缩", "任务栏空档放不下时，先把媒体条压缩到“最小宽度”，而不是整条隐藏。", "\uE7C4",
            () => _store.Current.Bar.ShrinkWhenTight, v => _store.Current.Bar.ShrinkWhenTight = v);

        Border minRow = Number("最小宽度", "允许压缩到的最小宽度。仅在“空档不够时压缩”开启时生效。", "\uE8D2", 160, 800, 20,
            () => _store.Current.Bar.MinimumWidthDip,
            v =>
            {
                int cap = _store.Current.Bar.WidthDip;
                _store.Current.Bar.MinimumWidthDip = (int)Math.Min(v, cap);
            }, "DIP");
        LinkRow(minRow, () => _store.Current.Bar.ShrinkWhenTight);

        // 防呆提示：把"任务栏实际有多少空档"摊给用户看，放不下就说清后果（红字）
        Note("媒体条贴在任务栏上，自动避开已有按钮占用区；开启任务栏自动隐藏时会跟着收起。");
        _pageHost.Children.Add(_spaceNote);
        _linkage.Add(EnforceFixedWidthLimit);
        _linkage.Add(RefreshSpaceNote);
        EnforceFixedWidthLimit();
        RefreshSpaceNote();
        StartLiveTimer();
    }

    /// <summary>
    /// 每秒重测"随环境变化的值"（任务栏空档、可调上下限、歌词宽度上限）—— 只读一次会过期 ✗
    /// （用户看到"限位数值写错了"就是这个原因；歌词宽度上限同理：主程序的任务栏探针要几秒才就绪）
    /// </summary>
    private void StartLiveTimer()
    {
        _liveTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _liveTimer.Tick -= OnLiveTick;
        _liveTimer.Tick += OnLiveTick;
        _liveTimer.Start();
    }

    private void OnLiveTick(object? sender, EventArgs e)
    {
        EnforceFixedWidthLimit();
        EnforceLyricsWidthLimit();   // 歌词自定义宽度的上限（来自主程序实测的任务栏空档，可能晚几秒才有值）
        RefreshLyricsWidthNote();
        RefreshSpaceNote();
    }

    /// <summary>歌词页那句"上限是多少 + 怎么才能更大"（值来自主程序实测的 bar.maxTextDip）。</summary>
    private void RefreshLyricsWidthNote()
    {
        if (_lyricsWidthNote == null)
        {
            return;
        }

        int? maxText = ReadStateInt("maxTextDip");
        int? liveText = ReadStateInt("liveTextDip");
        string head = maxText is int v && v > 0
            ? $"歌词宽度上限：{v} DIP（= 任务栏空档 − 必需部件，即把频谱 / 控制按钮 / 进度条全关掉时能达到的最大值）。"
            : "歌词宽度上限：读取中…";
        string live = liveText is int l && l > 0 && maxText is int cap && l < cap
            ? $"　当前这套配置实际能放下 {l} DIP；关掉频谱 / 控制按钮 / 进度条，就能一路放到 {cap} DIP。"
            : string.Empty;
        _lyricsWidthNote.Text = head + live;
    }

    /// <summary>
    /// 空档提示 + 放不下告警 + **可调下限**。空档值来自 state.json 的 bar.maxFreeDip（主程序探测写入的实测值）。
    /// 这解决的是用户遇到过的陷阱：固定宽度 660 DIP > 空档 655 DIP 且关掉压缩 → 条子**直接消失** ✗
    /// 而界面上没有任何提示。
    /// </summary>
    private void RefreshSpaceNote()
    {
        int? maxFree = ReadMaxFreeDip();
        int? minTotal = ReadStateInt("minWidthDip");
        Configuration.BarSettings bar = _store.Current.Bar;

        string text = maxFree is int value
            ? $"当前任务栏最大可用空档：{value} DIP。"
            : "当前任务栏最大可用空档：读取中…";
        text += minTotal is int min
            ? $"「固定宽度」可调下限：{min} DIP（随当前曲目变化：无时间轴时更小）。"
            : string.Empty;
        // 「歌词自定义宽度」的实测上限（主程序按落位策略算出来的）—— 超出会被压回，这里把原因说清 ✓
        if (ReadStateInt("maxTextDip") is int maxText && maxText > 0)
        {
            text += $"「歌词自定义宽度」可调上限：{maxText} DIP（= 任务栏能让出的最大文字区宽度，再宽就会被压缩回这个值）。";
        }

        bool warning = false;
        if (bar.WidthMode == "fixed" && maxFree is int gap && bar.WidthDip > gap)
        {
            warning = true;
            text += bar.ShrinkWhenTight
                ? $"　固定宽度 {bar.WidthDip} DIP 超出空档，会被压缩到约 {gap} DIP。"
                : $"　固定宽度 {bar.WidthDip} DIP 超出空档，且“空档不够时压缩”未开启 → 媒体条将不显示。请调小固定宽度或开启压缩。";
        }

        _spaceNote.Text = text;
        _spaceNote.Foreground = new SolidColorBrush(warning
            ? (Dark ? Color.FromRgb(0xFF, 0x9A, 0x9A) : Color.FromRgb(0xC4, 0x2B, 0x1C))
            : (Dark ? Color.FromRgb(0xB4, 0xB4, 0xC0) : Color.FromRgb(0x5A, 0x5A, 0x66)));
    }

    /// <summary>
    /// 「固定宽度」的可调下限 = 整条的可行最小宽度（= 其余部件总宽 + 文字区最小宽度，由 bar 视图实测给出）。
    /// 用户调不到比它更小的值 ✓ —— 这比"允许调小、然后内容被裁/界面抽搐"要好（用户提出的方案 ✓）。
    /// 值由主程序写进 state.json 的 bar.minWidthDip。
    /// </summary>
    private void EnforceFixedWidthLimit()
    {
        int? minTotal = ReadStateInt("minWidthDip");
        if (minTotal is not int min)
        {
            return;
        }

        _fixedWidthMinDip = min;   // 下面那个 Number 的 setter 会用它当钳制下限

        Configuration.BarSettings bar = _store.Current.Bar;
        if (bar.WidthDip < min)
        {
            bar.WidthDip = min;
            if (_numberBoxes.TryGetValue("固定宽度", out TextBox? box))
            {
                box.Text = min.ToString();
            }
            _store.Save();
        }
    }

    /// <summary>
    /// 「歌词自定义宽度」的**动态上限** = 任务栏能给文字区的最大宽度（主程序实测写进 state.json 的 bar.maxTextDip）。
    /// 为什么必须钳：整条 = 文字区 + 其余部件，文字区设得超出空档 → 落位策略把整条**压缩**回空档 ✗
    /// 实测过：设 580 DIP，实际只拿到 ≈335 DIP ✗ = 假开关 ✗（用户按开关没反应最反感的就是这个）
    /// 所以直接压到能放下的最大值，并在提示条里报出这个上限 ✓
    /// 与 EnforceFixedWidthLimit 同一套做法（那个抬高到下限，这个压低到上限）。
    /// </summary>
    private void EnforceLyricsWidthLimit()
    {
        int? maxText = ReadStateInt("maxTextDip");
        Configuration.LyricsSettings lyrics = _store.Current.Lyrics;
        // 0 / 缺失 = 主程序还没测出来（任务栏探针未就绪）→ 不做钳制 ✓
        // 绝不能拿"未知"当上限去压用户的值 ✗：实测探针就绪前算出来的上限是 1555，被 clamp 成 800 ✗
        // 于是"580 也合法"→ 防呆整个失效（用户设多少都"看起来"合法，实际被落位策略压缩回 304）
        if (maxText is not int max || max <= 0)
        {
            return;
        }

        max = Math.Clamp(max, 120, 900);
        if (lyrics.WidthDip <= max)
        {
            return;
        }

        int from = lyrics.WidthDip;
        lyrics.WidthDip = max;
        if (_numberBoxes.TryGetValue("歌词自定义宽度", out TextBox? box))
        {
            box.Text = max.ToString();
        }

        Diagnostics.Log.Write("lyrics", $"[宽度防呆] 「歌词自定义宽度」超出任务栏上限 → {from} 压到 {max} DIP");
        _store.Save();
    }

    private static int? ReadStateInt(string field)
    {
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RyuBar",
                "state.json");
            if (!System.IO.File.Exists(path))
            {
                return null;
            }

            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("bar", out System.Text.Json.JsonElement barNode)
                && barNode.TryGetProperty(field, out System.Text.Json.JsonElement node)
                && node.TryGetDouble(out double v))
            {
                return (int)Math.Round(v);
            }
        }
        catch
        {
            // 状态文件可能正在被写
        }

        return null;
    }

    private static int? ReadMaxFreeDip()
    {
        return ReadStateInt("maxFreeDip");
    }

    private void BuildLookPage()
    {
        Header("外观", "媒体条本体、主题跟随与配色来源。");
        Group("主题");
        Toggle("跟随系统强调色", "开关、选中项、图标块都直接取系统的强调色；换壁纸自动换主题色时也会跟着变。关掉后固定使用默认蓝 #60A5FA。",
            "\uE790", () => _store.Current.Ui.FollowSystemAccent, v => _store.Current.Ui.FollowSystemAccent = v);
        Note($"当前系统强调色：#{SystemColors.AccentColor.R:X2}{SystemColors.AccentColor.G:X2}{SystemColors.AccentColor.B:X2}（第一方 SystemColors.AccentColor，系统里改了一刷新就变）");
        Group("媒体条");
        Toggle("环形进度", "绕在专辑图外圈的一圈进度（不影响媒体条宽度）。", "\uE9D9",
            () => _store.Current.Bar.RingProgress, v => _store.Current.Bar.RingProgress = v);
        Border ringSeekRow = Toggle("环上可拖动跳转", "在环形进度上按住拖动可以跳转进度。仅在显示环形进度且曲目可跳转时有效。", "\uE8C9",
            () => _store.Current.Bar.RingSeek, v => _store.Current.Bar.RingSeek = v);
        // ★ 补联动 ✗ 原来环形进度关掉后它还能点 ⇒ 像坏掉的开关 ✓（用户盘点时发现 ✓）
        LinkRow(ringSeekRow, () => _store.Current.Bar.RingProgress);
        Toggle("条状进度", "原来的横向进度条（关掉后媒体条会更窄）。", "\uE9D9",
            () => _store.Current.Bar.BarProgress, v => _store.Current.Bar.BarProgress = v);
        Toggle("控制按钮", "上一首 / 播放暂停 / 下一首（关掉后媒体条会更窄）。", "\uE768",
            () => _store.Current.Bar.ShowControls, v => _store.Current.Bar.ShowControls = v);
        RingColorRow();
        RingOpacityRow();
        RingThicknessRow();
        BackgroundOpacityRow();
        // 「频谱」：数据来自系统正在播放的声音（WASAPI 环回采集 + 自写 FFT）✓
        // 说明文字改过：原来写的是"当前是样子：波形为合成数据"✗ 现在已经是真数据了 ✓
        Toggle("频谱", "在歌词右侧显示一排频谱柱；数据来自系统正在播放的声音（环回采集 + 自写 FFT）。",
            "\uE9D9", () => _store.Current.Bar.Spectrum, v => _store.Current.Bar.Spectrum = v);
        // 柱数：柱子越少越粗（观感更干净）、越多越细（更像真频谱）✓ 关掉频谱时这一行变灰 ✓
        Border spectrumBarsRow = Number("频谱柱数", "这一排画几根柱子；越少越粗，越多越细。",
            "\uE8D2", 4, 16, 1, () => _store.Current.Bar.SpectrumBars, v => _store.Current.Bar.SpectrumBars = v, "根");
        LinkRow(spectrumBarsRow, () => _store.Current.Bar.Spectrum);
        Note("以上几项都可以任意组合，都关掉也能用（只剩封面、标题歌词与时间）；媒体条宽度会跟着变化。" +
       "环形进度与条状进度是两套独立的指示器，同时开启时两种都会显示。");
    }

    private void BuildLyricsPage()
    {
        var lyrics = _store.Current.Lyrics;
        Header("歌词", "行数、逐字高亮、配色与长行滚动。");
        Group("显示");
        Choice("显示行数", "两行 = 当前句 + 下一句。", "\uE8D6", new[] { "一行", "两行" },
            () => Math.Clamp(lyrics.Lines, 1, 2) - 1, i => lyrics.Lines = i + 1);
        Toggle("逐字高亮", "只在歌词源真的给了逐字时间轴时生效。", "\uE8D3", () => lyrics.PerWordHighlight, v => lyrics.PerWordHighlight = v);
        Toggle("滚动显示", "超出文字区的长句滚动显示。", "\uE8D6", () => lyrics.Marquee, v => lyrics.Marquee = v);
        // ★「歌词宽度自动适配」+「歌词自定义宽度」—— 互斥联动（用户要求 ✓）
        //   自动适配开 → 宽度由软件算（跟着歌词长短走，上限取决于任务栏空档）→ 自定义宽度那行变灰 ✓
        //   自动适配关 → 才应用自定义宽度 ✓
        Border lyricsAutoFitRow = Toggle("歌词宽度自动适配",
            "开启后文字区宽度由软件自动调整：短句收窄、长句放宽，上限取决于任务栏空闲区大小（不用配置）。关闭后才应用下面的“歌词自定义宽度”。",
            "\uE7C4", () => lyrics.AutoFitWidth, v => lyrics.AutoFitWidth = v);
        Border lyricsWidthRow = Number("歌词自定义宽度",
            "仅“歌词宽度自动适配”关闭时生效：文字区用这个固定宽度，超出的长句滚动显示。上限 = 这台机器能达到的最大值（任务栏实测）。",
            "\uE8D2", 120, 900, 20, () => lyrics.WidthDip, v => lyrics.WidthDip = v, "DIP",
            () => ReadStateInt("maxTextDip") is int m && m > 0 ? m : 900);
        // 「条子宽度 = 固定」时两行**一起变灰**（用户裁决 ①）：
        //   那种情况下文字区宽度是由整条宽度反推出来的（BarView：textWidth = 固定条宽 − 其余部件）✓
        //   这两个值都会被覆盖 ✗ → 灰掉比"点了没反应"诚实 ✓
        LinkRow(lyricsAutoFitRow, () => _store.Current.Bar.WidthMode != "fixed");
        LinkRow(lyricsWidthRow, () => !lyrics.AutoFitWidth && _store.Current.Bar.WidthMode != "fixed");

        // ★ 把"上限是多少 / 怎么才能更大"直接摊在歌词页 ✗ 别让用户对着 304 猜
        //   上限 = 任务栏空档 − 其它部件宽度（封面/按钮/频谱/进度条/时间/留白）
        //   所以唯一有效的变大办法是**把那些部件关掉**（宽度会让给歌词）
        //   —— 光把数字改大没用 ✗ 落位策略照样把整条压回空档 ✗（实测过：设 580 只拿到 335）
        _lyricsWidthNote = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 10),
        };
        _pageHost.Children.Add(_lyricsWidthNote);

        // 动态上限防呆：超出"任务栏能给文字区的最大宽度"的值会被压回 —— 否则就是个假开关 ✗
        _linkage.Add(EnforceLyricsWidthLimit);
        // ★ 必须**立刻**跑一次 ✗ _linkage 只在"用户改了设置之后"重跑 ✓
        //   不立刻跑的话，打开设置窗口看到的还是那个放不下的旧值（580）✗
        //   用户会照着这个值去点"自动适配"开关 → 打开就发现宽度和显示的不一样 ✗
        //   做法与「固定宽度」那行完全一致（它也是 Add 之后再显式调一次）
        EnforceLyricsWidthLimit();
        // 上限来自主程序的任务栏探针（要几秒才就绪 → 刚起来时读到的是 0=未知 ✗）
        // 所以不能只读一次：挂到每秒的重测上，探针就绪后自动纠正 ✓
        StartLiveTimer();
        SliderRow("歌词字号", "歌词与标题的字号倍率；与封面大小相互独立。", "\uE8D2",
            80, 150, 5,
            () => lyrics.FontScalePercent,
            v => lyrics.FontScalePercent = Math.Clamp(v, 80, 150),
            "%");
        SliderRow("封面大小", "封面图的尺寸；100% = 标准大小。与歌词字号相互独立；过大时会自动限制在条子高度内（不会被裁）。", "\uE790",
            60, 125, 5,
            () => lyrics.ArtSizePercent,
            v => lyrics.ArtSizePercent = Math.Clamp(v, 60, 125),
            "%");
        Choice("歌词对齐", "这一行短于文字区时靠哪边（长行滚动时总是从左开始）。", "\uE8A1",
            new[] { "左对齐", "居中", "右对齐" },
            () => Array.IndexOf(AlignValues, lyrics.Alignment) is var i && i >= 0 ? i : 0,
            i => lyrics.Alignment = AlignValues[Math.Clamp(i, 0, 2)]);
        Group("配色");
        ColorRow("未唱颜色", "还没唱到的部分。", () => lyrics.TextColor, v => lyrics.TextColor = v);
        ColorRow("已唱颜色", "逐字高亮的颜色（卡拉 OK 效果）。", () => lyrics.HighlightColor, v => lyrics.HighlightColor = v);
        ColorRow("下一句颜色", "第二行的颜色，通常更暗一些。", () => lyrics.NextLineColor, v => lyrics.NextLineColor = v);
        Group("内容");
        Toggle("过滤制作信息行", "丢弃作词/作曲/编曲等标注行，给正文腾出位置。", "\uE74D", () => lyrics.FilterInfoLines, v => lyrics.FilterInfoLines = v);
    }

    private void BuildNotifyPage()
    {
        var notify = _store.Current.Notification;
        Header("切歌通知", "换歌时弹出的图文通知。");
        Group("切歌通知");
        Toggle("显示切歌通知", "换歌时弹出封面 + 曲名 + 歌手。", "\uE7E7", () => notify.Enabled, v => notify.Enabled = v);
        Toggle("显示封面图", "关掉就只有两行文字。", "\uE91B", () => notify.ShowArtwork, v => notify.ShowArtwork = v);
        Group("位置与时长");
        Choice("通知落点", "通知相对屏幕的位置。", "\uE707", PositionOptions,
            () => Math.Max(Array.IndexOf(PositionValues, notify.Position), 0), i => notify.Position = PositionValues[i]);
        Number("停留时长", "通知停留多久后自动收起。", "\uE916", 1000, 10000, 500, () => notify.Milliseconds, v => notify.Milliseconds = v, "毫秒");
    }

    private void BuildInputPage()
    {
        var input = _store.Current.Interaction;
        Header("交互", "滚轮做什么、提示出现在哪里。");
        Group("滚轮");
        Choice("滚轮", "鼠标悬停在媒体条上滚动时做什么。", "\uE7C9", WheelOptions,
            () => Math.Max(Array.IndexOf(WheelValues, input.PrimaryWheelAction), 0), i => input.PrimaryWheelAction = WheelValues[i]);
        Choice("Ctrl + 滚轮", "按住 Ctrl 再滚动时做什么。", "\uE7C9", WheelOptions,
            () => Math.Max(Array.IndexOf(WheelValues, input.ChordWheelAction), 0), i => input.ChordWheelAction = WheelValues[i]);
        Group("提示条");
        Choice("提示落点", "滚轮提示条出现的位置。", "\uE707", PositionOptions,
            () => Math.Max(Array.IndexOf(PositionValues, input.TooltipPosition), 0), i => input.TooltipPosition = PositionValues[i]);
        Number("提示停留", "滚轮提示条停留多久。", "\uE916", 600, 5000, 200, () => input.TooltipMilliseconds, v => input.TooltipMilliseconds = v, "毫秒");
    }

    private void BuildAppPage()
    {
        Header("应用", "设置文件、高级项与退出。");
        Group("设置文件");
        ActionRow("打开设置目录", _store.FilePath, "\uE8B7", "打开目录", () =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.IO.Path.GetDirectoryName(_store.FilePath)!,
                    UseShellExecute = true,
                });
            }
            catch
            {
                // 忽略
            }
        });
        // ★ 删掉「高级 → 网易云位置偏移」✗
        //   那是内存扫描的内部产物：用户既看不懂也改不了，还暴露了实现方式 ✓
        //   需要时看日志即可（NetEaseProbe 会写）✓
        Group("关于");
        // ★ 版本号：从**程序集**读 ✗ 不在界面里写死 ✓
        //   这样以后只改 RyuBar.csproj 的 <Version> ⇒ 这里自动跟着变 ✓
        //   不会出现"文档一个版本、程序里另一个版本"（历史上就出现过：CHANGELOG 到 0.8.40 而代码一直是 0.1.0 ✗）
        _pageHost.Children.Add(new TextBlock
        {
            Text = $"RyuBar {ReadAppVersion()}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 3),
        });
        // ★ 界面只显示干净的版本号 ✗ 不带 git 哈希 ✓
        //   `0.8.41+fc668b3…` 对普通用户零价值，只在排查时有用 ⇒ 走 --diag 输出 ✓
        Note("RyuBar 设置 · 与媒体条同一个进程；点右上角关闭只是关掉本窗口，媒体条继续常驻。");

        Button quit = new()
        {
            Content = "退出 RyuBar",
            Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0),
        };
        quit.Click += (_, _) =>
        {
            MessageBoxResult answer = MessageBox.Show(
                this, "媒体条会一并关闭，之后需要重新启动程序。", "退出 RyuBar？",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK)
            {
                Application.Current.Shutdown();
            }
        };
        _pageHost.Children.Add(quit);
    }

    /// <summary>
    /// 读应用版本号（形如 `0.8.40` ✓ 段数取 3 ⇒ 与 `RyuBar.csproj` 的 &lt;Version&gt; 写法一致 ✓）。
    /// 从程序集读而不是写死 ✓ 保证界面与构建产物永远同源 ✓
    /// </summary>
    private static string ReadAppVersion()
    {
        try
        {
            Version? version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "未知" : version.ToString(3);
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>读信息性版本（含 git 提交哈希 ✓ 形如 `0.8.41+24049ca…` ✓；取不到就退回版本号）。</summary>
    private static string ReadAppInformationalVersion()
    {
        try
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            // 用 Attribute.GetCustomAttribute ✗ 不用扩展方法 GetCustomAttribute<T> ✓
            // （后者要求文件头部有 using System.Reflection ✓ 而这里只有一个调用点 ✓ 不值得为它牵动 using ✓）
            object? attribute = Attribute.GetCustomAttribute(
                assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            string? info = (attribute as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion;
            return string.IsNullOrWhiteSpace(info) ? ReadAppVersion() : info;
        }
        catch
        {
            return ReadAppVersion();
        }
    }
}
