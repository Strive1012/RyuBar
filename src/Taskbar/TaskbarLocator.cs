using System.Runtime.InteropServices;
using System.Text;
using RyuBar.Interop;

namespace RyuBar.Taskbar;

/// <summary>
/// 定位任务栏并读回几何。任务栏是 Explorer 拥有的窗口，所有读取都是"采样"语义：
/// 拿到的值只在本次采样时刻有效，调用方必须自己判断稳定性。
/// </summary>
public static class TaskbarLocator
{
    public const string PrimaryClass = "Shell_TrayWnd";
    public const string SecondaryClass = "Shell_SecondaryTrayWnd";

    public static IntPtr FindPrimary()
    {
        return NativeMethods.FindWindow(PrimaryClass, null);
    }

    public static uint ProcessIdOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    public static string ClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "";
        }
        StringBuilder sb = new(256);
        _ = NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>任务栏客户区在屏幕坐标系下的矩形。子窗口的 (0,0) 就是它的左上角。</summary>
    public static bool TryGetClientScreenRect(IntPtr hwnd, out RECT rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetClientRect(hwnd, out RECT client))
        {
            return false;
        }
        POINT origin = new() { X = client.Left, Y = client.Top };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            return false;
        }
        rect = new RECT
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + client.Width,
            Bottom = origin.Y + client.Height,
        };
        return true;
    }

    public static RECT MonitorRectOf(IntPtr hwnd)
    {
        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        MONITORINFO info = new() { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return info.Monitor;
        }
        return default;
    }

    public static double ScaleOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 1.0;
        }
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// 自动隐藏的任务栏滑出屏幕后窗口仍然存在，只是与显示器不再相交。
    /// 用"与显示器可见高度"判断隐藏，比 IsWindowVisible 可靠。
    /// </summary>
    public static bool IsHidden(in RECT windowRect, in RECT monitorRect)
    {
        if (windowRect.IsEmpty)
        {
            return true;
        }
        int visibleHeight = Math.Min(windowRect.Bottom, monitorRect.Bottom) - Math.Max(windowRect.Top, monitorRect.Top);
        int visibleWidth = Math.Min(windowRect.Right, monitorRect.Right) - Math.Max(windowRect.Left, monitorRect.Left);
        if (monitorRect.IsEmpty)
        {
            return visibleHeight <= 4;
        }
        return visibleHeight <= 4 || visibleWidth <= 4;
    }
}
