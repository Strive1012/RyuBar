using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using RyuBar.Diagnostics;

namespace RyuBar.SystemIntegration;

/// <summary>
/// 读取并**实时跟随** Windows 的主题（强调色 + 深/浅色）。
///
/// 强调色来源（按可靠性排序）：
///   ① 注册表 <c>Explorer\Accent\AccentPalette</c> 的第 8~10 字节 —— Win11 真正用于强调的色值；
///   ② 注册表 <c>DWM\AccentColor</c>（ABGR）；
///   ③ <c>SystemParameters.WindowGlassColor</c>。
///
/// **实时性**：Windows 在强调色变化时向所有顶层窗口广播
/// <c>WM_DWMCOLORIZATIONCOLORCHANGED</c>（壁纸引擎换壁纸自动提色就是走这条），
/// 所以挂上消息钩子即可立刻跟随；另外每 5 秒兜底轮询一次，
/// 因为某些改动方式不一定广播。
/// </summary>
public static class SystemTheme
{
    private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WM_SETTINGCHANGE = 0x001A;

    private static readonly object Gate = new();
    private static HwndSource? _source;
    private static System.Windows.Threading.DispatcherTimer? _poll;
    private static Color _accent = Color.FromRgb(0x60, 0xA5, 0xFA);
    private static bool _light;

    /// <summary>强调色或深浅色变化时触发（可能不在 UI 线程）。</summary>
    public static event Action? Changed;

    /// <summary>当前的系统强调色（读不到时退回我们原来的蓝）。</summary>
    public static Color Accent
    {
        get
        {
            lock (Gate)
            {
                return _accent;
            }
        }
    }

    /// <summary>系统是否为浅色（应用主题）。</summary>
    public static bool IsLight
    {
        get
        {
            lock (Gate)
            {
                return _light;
            }
        }
    }

    /// <summary>挂到一个顶层窗口上：接收 DWM 广播 + 启动兜底轮询。只挂一次。</summary>
    public static void Attach(Window? window)
    {
        Refresh();
        if (_source != null)
        {
            return;
        }

        try
        {
            if (window != null)
            {
                nint handle = new WindowInteropHelper(window).Handle;
                HwndSource? source = HwndSource.FromHwnd(handle);
                if (source != null)
                {
                    source.AddHook(Hook);
                    _source = source;
                }
            }

            if (_poll == null && System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            {
                _poll = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Background, dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(5),
                };
                _poll.Tick += (_, _) => Refresh();
                _poll.Start();
            }
        }
        catch (Exception ex)
        {
            Log.Write("theme", "跟随系统主题初始化失败（忽略）", ex);
        }
    }

    private static nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is WM_DWMCOLORIZATIONCOLORCHANGED or WM_THEMECHANGED or WM_SETTINGCHANGE)
        {
            Refresh();
        }
        return IntPtr.Zero;
    }

    /// <summary>重新读取；有变化才通知订阅者。</summary>
    public static void Refresh()
    {
        Color accent = ReadAccent();
        bool light = ReadLight();

        bool changed;
        lock (Gate)
        {
            changed = accent != _accent || light != _light;
            _accent = accent;
            _light = light;
        }

        if (changed)
        {
            Log.Write("theme", $"系统主题变化: 强调色 #{accent.R:X2}{accent.G:X2}{accent.B:X2} 浅色={light}");
            Changed?.Invoke();
        }
    }

    private static Color ReadAccent()
    {
        // ① Win11 强调色面板：AccentPalette 的偏移 8 处是"Accent"本身
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 12)
            {
                Color color = Color.FromRgb(palette[8], palette[9], palette[10]);
                if (color.R + color.G + color.B > 24)
                {
                    return color;
                }
            }
        }
        catch
        {
            // 读不到就走下一个来源
        }

        // ② DWM\AccentColor 是 ABGR 排列
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int packed)
            {
                uint value = unchecked((uint)packed);
                Color color = Color.FromRgb((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
                if (color.R + color.G + color.B > 24)
                {
                    return color;
                }
            }
        }
        catch
        {
            // 读不到就走下一个来源
        }

        // ③ WPF 现成的窗口玻璃色
        try
        {
            Color glass = System.Windows.SystemParameters.WindowGlassColor;            if (glass.R + glass.G + glass.B > 24)
            {
                return glass;
            }
        }
        catch
        {
            // 忽略
        }

        return Color.FromRgb(0x60, 0xA5, 0xFA);
    }

    private static bool ReadLight()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value != 0;
            }
        }
        catch
        {
            // 忽略
        }
        return false;
    }

    /// <summary>把强调色调亮/调暗（用于悬停、按下、禁用等派生色）。</summary>
    public static Color Shade(Color color, double factor)
    {
        static byte Clamp(double value) => (byte)Math.Clamp(value, 0, 255);
        return Color.FromRgb(
            Clamp(color.R * factor),
            Clamp(color.G * factor),
            Clamp(color.B * factor));
    }

    /// <summary>给强调色叠一层透明度。</summary>
    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
