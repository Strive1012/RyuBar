# Inspect the RyuBar taskbar host: window region, hit-test owners, and click pass-through.
# ASCII-only on purpose: Windows PowerShell 5.1 reads UTF-8 without BOM as ANSI and mangles non-ASCII.
$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class HostProbe
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int m);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr h, IntPtr rgn);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] public static extern int GetRgnBox(IntPtr rgn, out RECT box);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);

    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    public const uint LD = 0x0002;
    public const uint LU = 0x0004;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L; public int T; public int R; public int B; }

    public static List<IntPtr> WindowsOf(uint pid)
    {
        List<IntPtr> list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p == pid) { list.Add(h); }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string RegionOf(IntPtr h)
    {
        IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
        int kind = GetWindowRgn(h, rgn);
        RECT box;
        GetRgnBox(rgn, out box);
        DeleteObject(rgn);
        string k;
        if (kind == 0) { k = "ERROR"; }
        else if (kind == 1) { k = "NULLREGION"; }
        else if (kind == 2) { k = "SIMPLEREGION"; }
        else if (kind == 3) { k = "COMPLEXREGION"; }
        else { k = "kind" + kind; }
        return string.Format("kind={0} box=({1},{2})-({3},{4}) {5}x{6}",
            k, box.L, box.T, box.R, box.B, box.R - box.L, box.B - box.T);
    }

    public static string OwnerAt(int x, int y)
    {
        POINT p;
        p.X = x;
        p.Y = y;
        IntPtr h = WindowFromPoint(p);
        StringBuilder cn = new StringBuilder(128);
        GetClassName(h, cn, 128);
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        return string.Format("0x{0:X} {1} pid={2}", h.ToInt64(), cn.ToString(), pid);
    }

    public static string Foreground()
    {
        IntPtr h = GetForegroundWindow();
        StringBuilder t = new StringBuilder(256);
        GetWindowText(h, t, 256);
        StringBuilder cn = new StringBuilder(128);
        GetClassName(h, cn, 128);
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        return string.Format("pid={0} {1} '{2}'", pid, cn.ToString(), t.ToString());
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(LD, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        mouse_event(LU, 0, 0, 0, IntPtr.Zero);
    }
}
'@

[void][HostProbe]::SetProcessDpiAwarenessContext([IntPtr](-4))

$proc = Get-Process RyuBar -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'RyuBar is not running'; exit 1 }
Write-Output ("RyuBar pid = " + $proc.Id)

Write-Output ''
Write-Output '== window regions =='
foreach ($h in [HostProbe]::WindowsOf([uint32]$proc.Id)) {
    $cls = New-Object System.Text.StringBuilder 128
    [void][HostProbe]::GetClassName($h, $cls, 128)
    Write-Output ("  hwnd=0x{0:X}  class={1}  {2}" -f $h.ToInt64(), $cls.ToString(), [HostProbe]::RegionOf($h))
}

Write-Output ''
Write-Output '== hit test with the taskbar revealed (cursor kept at the bottom edge) =='
[void][HostProbe]::SetCursorPos(1280, 1599)
Start-Sleep -Seconds 2

$state = Get-Content "$env:LOCALAPPDATA\RyuBar\state.json" -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
if ($state) {
    Write-Output ("  state: attached={0} bar=({1},{2}) {3}x{4} reason={5}" -f $state.attached, $state.bar.left, $state.bar.top, $state.bar.width, $state.bar.height, $state.reason)
    $barLeft = [int]$state.bar.left
    $barRight = $barLeft + [int]$state.bar.width
} else {
    Write-Output '  no state.json'
    $barLeft = 12
    $barRight = 642
}

$y = 1564
Write-Output ("  inside  bar ({0},{1}) : {2}" -f ($barLeft + 100), $y, [HostProbe]::OwnerAt($barLeft + 100, $y))
Write-Output ("  inside  bar ({0},{1}) : {2}" -f ($barRight - 60), $y, [HostProbe]::OwnerAt($barRight - 60, $y))
Write-Output ("  outside bar ({0},{1}) : {2}" -f ($barRight + 80), $y, [HostProbe]::OwnerAt($barRight + 80, $y))
Write-Output ("  outside bar (1215,{0}) : {1}" -f $y, [HostProbe]::OwnerAt(1215, $y))

Write-Output ''
Write-Output '== click pass-through =='
[void][HostProbe]::SetCursorPos(1280, 1599)
Start-Sleep -Seconds 2
$before = [HostProbe]::Foreground()
Write-Output ("  before        : " + $before)
[HostProbe]::Click(1215, 1564)
Start-Sleep -Milliseconds 1200
$afterIcon = [HostProbe]::Foreground()
Write-Output ("  after (icon)  : " + $afterIcon)
[HostProbe]::Click($barLeft + 100, 1564)
Start-Sleep -Milliseconds 1200
$afterBar = [HostProbe]::Foreground()
Write-Output ("  after (bar)   : " + $afterBar)
[void][HostProbe]::SetCursorPos(1280, 1599)
