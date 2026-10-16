using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RyuBar.Interop;

namespace RyuBar.Components;

/// <summary>
/// 提示窗落点。前六个与参照实现的通知位置同名同义（它那边的枚举就是这六个），
/// 第七个 <see cref="AboveArtwork"/> 是按用户要求加的"左对齐专辑封面、浮在媒体条上方"。
/// </summary>
public enum TooltipPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,

    /// <summary>左边缘对齐专辑封面，浮在媒体条上方（贴着条的那种）。</summary>
    AboveArtwork,
}

/// <summary>
/// 浮动提示窗（对应参照实现的滚轮提示条 / 曲目切换通知）。
///
/// 特征：
///   * **永不激活**（`WS_EX_NOACTIVATE` + `ShowActivated=false` + 吞掉 `WM_MOUSEACTIVATE`），
///     所以弹出时不会打断你正在打字的窗口；
///   * 置顶但不抢焦点，鼠标移入立即隐藏；
///   * **复用一个窗口实例**（反复切换时不做新建/销毁）；
///   * 两行内容：第一行是动作名，第二行是结果（来源名 / 曲名）；
///   * 落点**可配置**（<see cref="TooltipPosition"/>），算法照搬参照实现的通知定位。
/// </summary>
public sealed class TooltipWindow : Window
{
    /// <summary>屏幕边距（DIP），与参照实现一致。</summary>
    private const double MarginDip = 16;

    private const double GapDip = 8;

    private readonly TextBlock _actionText;
    private readonly TextBlock _detailText;
    private readonly Border _card;
    private readonly Border _cover;
    private readonly Grid _layout;
    private DispatcherTimer? _hideTimer;
    private bool _hooked;

    /// <summary>封面尺寸（DIP）与圆角，对着媒体条的封面比例。</summary>
    private const double CoverSizeDip = 44;

    private const double CoverRadiusDip = 5;

    public TooltipWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        SnapsToDevicePixels = false;

        _actionText = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0x9A, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
        };
        _detailText = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
            Margin = new Thickness(0, 2, 0, 0),
        };

        StackPanel stack = new();
        stack.Children.Add(_actionText);
        stack.Children.Add(_detailText);

        // 封面：用 ImageBrush 铺满带圆角的 Border（图片本身没法直接圆角）
        _cover = new Border
        {
            Width = CoverSizeDip,
            Height = CoverSizeDip,
            CornerRadius = new CornerRadius(CoverRadiusDip),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 10, 0),
        };

        // 内容布局是"封面（可隐藏）+ 两行文字"，一个 Grid 同时支撑提示与图文通知两种形态
        _layout = new Grid();
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_cover, 0);
        Grid.SetColumn(stack, 1);
        _layout.Children.Add(_cover);
        _layout.Children.Add(stack);

        _card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x14, 0x18, 0x21)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Child = _layout,
        };
        Content = _card;

        MouseEnter += (_, _) => HideNow();
    }

    /// <summary>
    /// 显示提示 / 切歌通知。
    /// <paramref name="artwork"/> 非空时为**图文通知**（封面 + 曲名 + 副标题）；
    /// 传 null 就是原来的两行文字提示。
    /// <paramref name="anchorPx"/> 在 <see cref="TooltipPosition.AboveArtwork"/> 时用来定位（屏幕物理像素）。
    /// </summary>
    public void Show(
        string action,
        string detail,
        TooltipPosition position,
        Rect anchorPx,
        double scale,
        int holdMilliseconds,
        ImageSource? artwork = null)
    {
        bool withArtwork = artwork != null;
        _cover.Visibility = withArtwork ? Visibility.Visible : Visibility.Collapsed;
        if (withArtwork)
        {
            _cover.Background = new ImageBrush(artwork)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };
        }
        else
        {
            _cover.Background = null;
        }

        // 两种形态的主次正好相反：
        //   文字提示：第一行是动作名（小灰），第二行是结果（大亮）；
        //   图文通知：第一行是曲名（大亮），第二行是"歌手 · 来源"（小灰）。
        _actionText.Text = action;
        _actionText.FontSize = withArtwork ? 13.5 : 11;
        _actionText.FontWeight = withArtwork ? FontWeights.SemiBold : FontWeights.Normal;
        _actionText.Foreground = withArtwork
            ? new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x9A, 0xFF, 0xFF, 0xFF));
        _actionText.MaxWidth = withArtwork ? 300 : 260;

        _detailText.Text = detail;
        _detailText.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        _detailText.FontSize = withArtwork ? 11.5 : 13;
        _detailText.FontWeight = withArtwork ? FontWeights.Normal : FontWeights.SemiBold;
        _detailText.Foreground = withArtwork
            ? new SolidColorBrush(Color.FromArgb(0x9A, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
        _detailText.MaxWidth = withArtwork ? 300 : 260;
        _detailText.Margin = withArtwork ? new Thickness(0, 3, 0, 0) : new Thickness(0, 2, 0, 0);

        if (!_hooked)
        {
            _hooked = true;
            SourceInitialized += OnSourceInitialized;
            if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
            {
                ApplyNoActivate();
            }
        }

        // 先布局，才能拿到真实尺寸来定位
        UpdateLayout();
        double width = Math.Max(ActualWidth, 80);
        double height = Math.Max(ActualHeight, 28);
        (double left, double top) = Place(position, anchorPx, width, height, scale);
        Left = left;
        Top = top;

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        // 重新量一次并落点：窗口首次显示前 ActualWidth/Height 不可靠，而且文案长度会变，
        // 每次显示都按真实尺寸定位才稳。这之后才是"用户真正看到"的落点。
        UpdateLayout();
        double finalWidth = Math.Max(ActualWidth, 80);
        double finalHeight = Math.Max(ActualHeight, 28);
        (double againLeft, double againTop) = Place(position, anchorPx, finalWidth, finalHeight, scale);
        Left = againLeft;
        Top = againTop;
        RyuBar.Diagnostics.Log.Write(
            "input",
            $"提示落点 {position} → 左上角 ({againLeft:0}, {againTop:0}) DIP，尺寸 {finalWidth:0}x{finalHeight:0}");

        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = Opacity,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(110),
            FillBehavior = FillBehavior.HoldEnd,
        });

        _hideTimer ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(holdMilliseconds);
        _hideTimer.Tick -= OnHideTick;
        _hideTimer.Tick += OnHideTick;
        _hideTimer.Start();
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        HideNow();
    }
    /// <summary>立刻收起（鼠标移入、或宿主条隐藏时调用）。</summary>
    public void HideNow()
    {
        _hideTimer?.Stop();
        if (!IsVisible)
        {
            return;
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(90),
            FillBehavior = FillBehavior.HoldEnd,
        });
        // 动画结束后再真正隐藏；这里用一个短延时，避免每帧都在动
        DispatcherTimer done = new(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(110),
        };
        done.Tick += (_, _) =>
        {
            done.Stop();
            Hide();
        };
        done.Start();
    }

    /// <summary>
    /// 计算落点。
    ///
    /// 六个屏幕位置（TopLeft/TopCenter/TopRight/BottomLeft/BottomCenter/BottomRight）的算法
    /// **照搬参照实现的 <c>NotificationPlacementCalculator.Calculate</c>**：工作区内 16 DIP 边距，
    /// 横向左/中/右、纵向上/下，最后统一夹进工作区。
    /// 另有 <see cref="TooltipPosition.AboveArtwork"/>：左边缘对齐封面、浮在媒体条上方。
    /// </summary>
    private (double Left, double Top) Place(
        TooltipPosition position,
        Rect anchorPx,
        double width,
        double height,
        double scale)
    {
        System.Windows.Rect work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
        {
            return (0, 0);
        }

        double minX = work.Left + MarginDip;
        double maxX = Math.Max(minX, work.Right - MarginDip - width);
        double minY = work.Top + MarginDip;
        double maxY = Math.Max(minY, work.Bottom - MarginDip - height);

        double left;
        double top;

        if (position == TooltipPosition.AboveArtwork)
        {
            double anchorLeft = anchorPx.Left / scale;
            double anchorTop = anchorPx.Top / scale;
            double anchorHeight = anchorPx.Height / scale;

            left = Math.Clamp(anchorLeft, minX, maxX);          // 左对齐封面左边缘
            top = anchorTop - GapDip - height;                  // 浮在媒体条上方
            if (top < work.Top + 2)
            {
                top = anchorTop + anchorHeight + GapDip;        // 上方不够就落到条下方
            }
        }
        else
        {
            left = position switch
            {
                TooltipPosition.TopCenter or TooltipPosition.BottomCenter => work.Left + (work.Width - width) / 2,
                TooltipPosition.TopRight or TooltipPosition.BottomRight => maxX,
                _ => minX,
            };
            top = position is TooltipPosition.TopLeft or TooltipPosition.TopCenter or TooltipPosition.TopRight
                ? minY
                : maxY;
        }

        return (Math.Clamp(left, minX, maxX), Math.Clamp(top, minY, maxY));
    }

    /// <summary>把设置里的字符串解析成落点；认不出就用默认（对齐封面）。</summary>
    public static TooltipPosition ParsePosition(string? value, TooltipPosition fallback = TooltipPosition.AboveArtwork)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "topleft" or "top-left" => TooltipPosition.TopLeft,
            "topcenter" or "top-center" or "center" => TooltipPosition.TopCenter,
            "topright" or "top-right" => TooltipPosition.TopRight,
            "bottomleft" or "bottom-left" => TooltipPosition.BottomLeft,
            "bottomcenter" or "bottom-center" => TooltipPosition.BottomCenter,
            "bottomright" or "bottom-right" => TooltipPosition.BottomRight,
            "aboveartwork" or "artwork" or "bar" => TooltipPosition.AboveArtwork,
            _ => fallback,
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ApplyNoActivate();

    private void ApplyNoActivate()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        _ = NativeMethods.SetWindowLong(
            hwnd,
            NativeMethods.GWL_EXSTYLE,
            exStyle | (int)NativeMethods.WS_EX_NOACTIVATE | (int)NativeMethods.WS_EX_TOOLWINDOW);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }
}
