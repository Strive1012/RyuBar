using System.Runtime.InteropServices;

namespace RyuBar.Interop;

/// <summary>
/// 托盘图标（纯 Win32 <c>Shell_NotifyIcon</c>，不引任何控件库）。
///
/// 图标不是资源文件，而是**用代码画的 32×32 像素**（圆角方块 + 三根音柱）→ 零素材依赖。
/// 交互：右键弹菜单（设置 / 退出）、双击打开设置；弹菜单用 <c>TrackPopupMenu</c>（原生观感、点外面自动收起）。
/// explorer 重启后必须重新 Add（宿主已处理 TaskbarCreated 消息，见 <see cref="RyuBarTaskbar"/> 的调用点）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>托盘回调消息：宿主 WndProc 里比对这个值。</summary>
    public static readonly int CallbackMessage = NativeMethods.WM_APP + 0x31;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int MF_STRING = 0x0000;
    private const int TPM_RIGHTBUTTON = 0x0002;
    private const int TPM_RETURNCMD = 0x0100;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const int CMD_SETTINGS = 1;
    private const int CMD_EXIT = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, int id, string text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, int flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(IntPtr hwnd, string tooltip)
    {
        _hwnd = hwnd;
        _icon = CreateIcon();
        Add(tooltip);
    }

    /// <summary>添加（explorer 重启后要重新调用）。</summary>
    public void Add(string tooltip)
    {
        NOTIFYICONDATA data = new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = tooltip,
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        Diagnostics.Log.Write("tray", $"Shell_NotifyIcon(NIM_ADD) 返回 {_added}（true = 通知区已接受）");
    }

    /// <summary>宿主收到 <see cref="CallbackMessage"/> 时调用；返回 true 表示已处理。</summary>
    public bool HandleMessage(int msg, Action openSettings, Action exit)
    {
        switch (msg)
        {
            case WM_LBUTTONDBLCLK:
                openSettings();
                return true;
            case WM_LBUTTONUP:
                return true;   // 单击不做事（避免误触）
            case WM_RBUTTONUP:
                ShowMenu(openSettings, exit);
                return true;
            default:
                return false;
        }
    }

    private void ShowMenu(Action openSettings, Action exit)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = AppendMenu(menu, MF_STRING, CMD_SETTINGS, "设置");
            _ = AppendMenu(menu, MF_STRING, CMD_EXIT, "退出 RyuBar");

            if (!GetCursorPos(out POINT point))
            {
                return;
            }

            // TrackPopupMenu 前先把宿主设为前台，否则菜单点了外面不会收起
            _ = SetForegroundWindow(_hwnd);
            int command = TrackPopupMenu(
                menu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);

            switch (command)
            {
                case CMD_SETTINGS:
                    openSettings();
                    break;
                case CMD_EXIT:
                    exit();
                    break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    /// <summary>用代码画一个 32×32 图标：圆角方块底 + 三根音柱。</summary>
    private static IntPtr CreateIcon()
    {
        const int size = 32;
        byte[] pixels = new byte[size * size * 4];   // BGRA，自下而上

        static bool InRoundedRect(int x, int y, int left, int top, int right, int bottom, int radius)
        {
            if (x < left || x > right || y < top || y > bottom)
            {
                return false;
            }
            int cx = x < left + radius ? left + radius : (x > right - radius ? right - radius : x);
            int cy = y < top + radius ? top + radius : (y > bottom - radius ? bottom - radius : y);
            int dx = x - cx;
            int dy = y - cy;
            return (dx * dx + dy * dy) <= radius * radius;
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int flipped = size - 1 - y;
                int index = ((flipped * size) + x) * 4;

                byte b = 0;
                byte g = 0;
                byte r = 0;
                byte a = 0;

                // 圆角方块底：不用纯黑，用深蓝灰，任何主题下都看得见
                if (InRoundedRect(x, y, 1, 1, size - 2, size - 2, 8))
                {
                    b = 0x2A;
                    g = 0x18;
                    r = 0x14;
                    a = 0xFF;
                }

                // 三根音柱（中间最高，像播放中的均衡器）
                int[] heights = { 9, 16, 12 };
                for (int i = 0; i < 3; i++)
                {
                    int barLeft = 8 + (i * 6);
                    int barRight = barLeft + 3;
                    int barTop = 23 - heights[i];
                    if (x >= barLeft && x <= barRight && y >= barTop && y <= 23)
                    {
                        b = 0xFF;    // 强调色（暖蓝）
                        g = 0xA5;
                        r = 0x60;
                        a = 0xFF;
                    }
                }

                pixels[index] = b;
                pixels[index + 1] = g;
                pixels[index + 2] = r;
                pixels[index + 3] = a;
            }
        }

        // 直接构造 HICON：CreateIconIndirect + 32 位 DIB
        BITMAPINFO info = new()
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr color = CreateDIBitmap(screen, ref info.bmiHeader, 4, pixels, ref info, 0);
        IntPtr mask = CreateBitmap(size, size, 1, 1, new byte[size * size / 8]);
        ReleaseDC(IntPtr.Zero, screen);

        ICONINFO iconInfo = new()
        {
            fIcon = true,
            hbmColor = color,
            hbmMask = mask,
        };
        IntPtr icon = CreateIconIndirect(ref iconInfo);

        _ = DeleteObject(color);
        _ = DeleteObject(mask);
        return icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBitmap(IntPtr dc, ref BITMAPINFOHEADER header, int init, byte[] bits, ref BITMAPINFO info, int usage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, int planes, int bitCount, byte[] bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    public void Dispose()
    {
        if (_added)
        {
            NOTIFYICONDATA data = new()
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
            };
            _ = Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            _ = DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }
}
