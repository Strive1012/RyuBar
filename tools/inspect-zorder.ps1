# Inspect the taskbar's CHILD windows in Z-order, newest-first, marking RyuBar's own host window.
# This is the tool for "is something covering our bar" questions: the taskbar's XAML content bridge
# (Windows.UI.Composition.DesktopWindowContentBridge) is a SIBLING of our host window, and anything
# in front of us paints over the bar.
# ASCII-only for Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class ZOrder
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int m);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    public delegate bool EnumProc(IntPtr h, IntPtr lp);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L; public int T; public int R; public int B; }

    public static List<string> Children(IntPtr taskbar, uint myPid)
    {
        List<string> rows = new List<string>();
        int index = 0;
        EnumChildWindows(taskbar, delegate(IntPtr h, IntPtr lp)
        {
            StringBuilder cn = new StringBuilder(160);
            GetClassName(h, cn, 160);
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            RECT r;
            GetWindowRect(h, out r);
            bool vis = IsWindowVisible(h);
            string mark = pid == myPid ? "   <<< OURS" : "";
            rows.Add(string.Format("{0,2}  0x{1:X}  vis={2}  ({3},{4}) {5}x{6}  pid={7}  {8}{9}",
                index, h.ToInt64(), vis ? 1 : 0, r.L, r.T, r.R - r.L, r.B - r.T, pid, cn.ToString(), mark));
            index++;
            return true;
        }, IntPtr.Zero);
        return rows;
    }
}
'@

[void][ZOrder]::SetProcessDpiAwarenessContext([IntPtr](-4))

$proc = Get-Process RyuBar -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'RyuBar is not running'; exit 1 }
$myPid = [uint32]$proc.Id

# reveal the taskbar
[void][ZOrder]::SetCursorPos(1280, 1599)
Start-Sleep -Seconds 3

$taskbar = [ZOrder]::FindWindow('Shell_TrayWnd', $null)
Write-Output ("RyuBar pid=" + $myPid + "   taskbar=0x" + $taskbar.ToInt64().ToString('X'))
Write-Output ''
Write-Output 'taskbar children, front-most first (index 0 = top of the Z-order):'
foreach ($row in [ZOrder]::Children($taskbar, $myPid)) { Write-Output ('  ' + $row) }
