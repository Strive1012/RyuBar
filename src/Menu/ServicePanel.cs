using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using RyuBar.Components;
using RyuBar.Media;

namespace RyuBar.Menu;

/// <summary>
/// 条子上方的二级面板（用户："单击歌词后在 bar 的上方调出二级菜单：进度条、音量调节（单应用）"✓）。
///
/// ★ 为什么必须是**独立顶层窗口** ✗ 不能画在条子里 ✓
///   条子是 `Shell_TrayWnd` 的 `WS_CHILD` 子窗口 ⇒ 它的绘制被**裁剪在父窗口客户区**内 ✗
///   再怎么画也出不了任务栏那 72px ⇒ 想要"浮在条子上方"只能另开一个 HWND ✓
///
/// 外观对齐 AF Media Bar 的参考图（用户提供 ✓）：
///   大封面 + 标题 / 歌手 / 来源 / 歌词来源
///   全宽进度条 + 左右时间
///   ⏮ ⏸ ⏭ 三个控制键
///   右侧：当前应用音量 + 滑块
/// 尺寸取紧凑版（约 420 逻辑像素高 ✓ 用户选择 ✓）而不是参考图的 560 ✓
/// </summary>
public sealed class ServicePanel : Window
{
    // ---- 配色（和条子同一套语汇 ✓）----
    private static readonly Brush PanelBackground = Frozen(Color.FromArgb(0xF2, 0x24, 0x28, 0x36));
    private static readonly Brush PanelBorder = Frozen(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PrimaryText = Frozen(Color.FromRgb(0xF2, 0xF4, 0xF8));
    private static readonly Brush SecondaryText = Frozen(Color.FromRgb(0x9A, 0xA3, 0xB8));
    private static readonly Brush TertiaryText = Frozen(Color.FromRgb(0x74, 0x7E, 0x96));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly Brush TrackBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush HoverBrush = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));

    private const double PanelWidth = 520;
    private const double PanelHeight = 420;
    private const double CornerRadius = 14;

    private readonly Image _artwork;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly TextBlock _source;
    private readonly ProgressBar _progress;
    private readonly TextBlock _position;
    private readonly TextBlock _duration;
    private readonly Button _previous;
    private readonly Button _playPause;
    private readonly Button _next;
    private readonly Slider _volume;
    private readonly TextBlock _volumeText;

    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private bool _dragging;

    /// <summary>用户在面板里拖动进度条（0..1 ✓）。</summary>
    public event Action<double>? SeekRequested;

    /// <summary>用户点了上一首 / 播放暂停 / 下一首（直接复用条上的 BarHit 语义 ✓）。</summary>
    public event Action<BarHit>? CommandInvoked;

    /// <summary>音量滑块被拖动（0..1 ✓ 已静音时也照常发 ✓）。</summary>
    public event Action<double>? VolumeChanged;

    /// <summary>点了静音按钮（true = 静音 ✓）。</summary>
    public event Action<bool>? MuteToggled;

    public ServicePanel()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = PanelWidth;
        Height = PanelHeight;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        Border shell = new()
        {
            Background = PanelBackground,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CornerRadius),
            Padding = new Thickness(20, 18, 20, 18),
        };

        StackPanel body = new();

        // ① 头部：封面 + 文字
        Grid header = new() { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _artwork = new Image
        {
            Width = 96,
            Height = 96,
            Stretch = Stretch.UniformToFill,
        };
        Border artFrame = new()
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(8),
            Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            ClipToBounds = true,
            Child = _artwork,
        };
        Grid.SetColumn(artFrame, 0);
        header.Children.Add(artFrame);

        StackPanel texts = new() { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _title = MakeText(17, FontWeights.SemiBold, PrimaryText);
        _artist = MakeText(13, FontWeights.Normal, SecondaryText);
        _source = MakeText(11.5, FontWeights.Normal, TertiaryText);
        _artist.Margin = new Thickness(0, 3, 0, 0);
        _source.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(_title);
        texts.Children.Add(_artist);
        texts.Children.Add(_source);
        Grid.SetColumn(texts, 1);
        header.Children.Add(texts);

        body.Children.Add(header);

        // ② 进度条 + 时间
        _progress = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 1,
            Foreground = AccentBrush,
            Background = TrackBrush,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        _progress.PreviewMouseLeftButtonDown += OnProgressDown;
        _progress.PreviewMouseMove += OnProgressMove;
        _progress.PreviewMouseLeftButtonUp += OnProgressUp;

        Grid timeRow = new() { Margin = new Thickness(0, 6, 0, 14) };
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _position = MakeText(11.5, FontWeights.Normal, SecondaryText);
        _duration = MakeText(11.5, FontWeights.Normal, SecondaryText);
        _duration.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_position, 0);
        Grid.SetColumn(_duration, 1);
        timeRow.Children.Add(_position);
        timeRow.Children.Add(_duration);

        body.Children.Add(_progress);
        body.Children.Add(timeRow);

        // ③ 三个控制键
        StackPanel controls = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 18),
        };
        _previous = MakeControlButton(Glyph.Previous, () => CommandInvoked?.Invoke(BarHit.Previous), "上一首");
        _playPause = MakeControlButton(Glyph.Play, () => CommandInvoked?.Invoke(BarHit.PlayPause), "播放/暂停");
        _next = MakeControlButton(Glyph.Next, () => CommandInvoked?.Invoke(BarHit.Next), "下一首");
        controls.Children.Add(_previous);
        controls.Children.Add(_playPause);
        controls.Children.Add(_next);
        body.Children.Add(controls);

        // ④ 当前应用音量
        Grid volumeRow = new();
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        TextBlock volumeLabel = MakeText(12.5, FontWeights.Normal, SecondaryText);
        volumeLabel.Text = "当前应用音量";
        volumeLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(volumeLabel, 0);
        volumeRow.Children.Add(volumeLabel);

        _volumeText = MakeText(12.5, FontWeights.Normal, PrimaryText);
        _volumeText.MinWidth = 44;
        _volumeText.TextAlignment = TextAlignment.Right;
        _volumeText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_volumeText, 1);
        volumeRow.Children.Add(_volumeText);

        _volume = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            SmallChange = 0.01,
            LargeChange = 0.1,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = AccentBrush,
        };
        _volume.ValueChanged += (_, _) =>
        {
            _volumeText.Text = $"{Math.Round(_volume.Value * 100)}%";
            if (!_dragging)
            {
                VolumeChanged?.Invoke(_volume.Value);   // 点击轨道也要生效 ✓ 拖动时由下面统一发 ✓
            }
        };

        StackPanel volumeBlock = new();
        volumeBlock.Children.Add(volumeRow);
        volumeBlock.Children.Add(_volume);
        body.Children.Add(volumeBlock);

        shell.Child = body;
        Content = shell;

        // 点面板外就关（Sh owActivated 默认 true ⇒ 会拿到 Deactivated ✓）
        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Hide();
            }
        };
    }

    /// <summary>把面板摆到条子正上方并显示（用户："在 bar 的上方"✓）。</summary>
    public void ShowAbove(Rect barScreenRect, MediaSnapshot snapshot, double volume)
    {
        Update(snapshot, volume);
        Left = barScreenRect.Left + ((barScreenRect.Width - Width) / 2);
        Top = barScreenRect.Top - Height - 8;
        Left = Math.Max(8, Math.Min(Left, SystemParameters.WorkArea.Right - Width - 8));
        Top = Math.Max(8, Top);
        Show();
        Activate();
    }

    /// <summary>刷新内容（宿主每轮轮询调 ✓ 面板开着时保持同步 ✓）。</summary>
    public void Update(MediaSnapshot snapshot, double volume)
    {
        _snapshot = snapshot;

        _artwork.Source = snapshot.Artwork;
        _title.Text = snapshot.DisplayTitle;
        _artist.Text = string.IsNullOrWhiteSpace(snapshot.Artist) ? "—" : snapshot.Artist;
        _source.Text = $"来源：{SourceNames.Friendly(snapshot.Source)}";

        DateTimeOffset now = DateTimeOffset.Now;
        if (!_dragging)
        {
            _progress.Value = snapshot.ProgressAt(now);
            _position.Text = BarView.Format(snapshot.PositionAt(now));
        }
        _duration.Text = snapshot.Duration > TimeSpan.Zero ? BarView.Format(snapshot.Duration) : "--:--";

        _playPause.Tag = snapshot.IsPlaying ? Glyph.Pause : Glyph.Play;
        _previous.IsEnabled = snapshot.CanPrevious;
        _playPause.IsEnabled = snapshot.CanPlayPause;
        _next.IsEnabled = snapshot.CanNext;

        if (!_volume.IsMouseCaptureWithin)
        {
            _volume.Value = Math.Clamp(volume, 0, 1);
            _volumeText.Text = $"{Math.Round(_volume.Value * 100)}%";
        }
    }

    // ---------------------------------------------------------------- 进度拖动

    private void OnProgressDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = true;
        _progress.CaptureMouse();
        ApplyProgress(e);
    }

    private void OnProgressMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging)
        {
            ApplyProgress(e);
        }
    }

    private void OnProgressUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        double ratio = ApplyProgress(e);
        _dragging = false;
        _progress.ReleaseMouseCapture();
        SeekRequested?.Invoke(ratio);
    }

    private double ApplyProgress(System.Windows.Input.MouseEventArgs e)
    {
        double width = _progress.ActualWidth;
        double ratio = width <= 1 ? 0 : Math.Clamp(e.GetPosition(_progress).X / width, 0, 1);
        _progress.Value = ratio;
        if (_snapshot.Duration > TimeSpan.Zero)
        {
            _position.Text = BarView.Format(TimeSpan.FromSeconds(_snapshot.Duration.TotalSeconds * ratio));
        }
        return ratio;
    }

    // ---------------------------------------------------------------- 小工具

    private static TextBlock MakeText(double size, FontWeight weight, Brush brush) => new()
    {
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private Button MakeControlButton(Func<Rect, Geometry> glyph, Action onClick, string tip)
    {
        Button button = new()
        {
            Width = 46,
            Height = 40,
            Margin = new Thickness(6, 0, 6, 0),
            Background = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tip,
            Content = new Path
            {
                Data = glyph(new Rect(0, 0, 16, 16)),
                Fill = PrimaryText,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Brush Frozen(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
