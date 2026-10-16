# Measure how the taskbar window actually moves when it reveals.
# Samples GetWindowRect at sub-millisecond resolution from inside a C# loop, triggers the reveal
# partway through, and prints the raw curve. Read-only: no code under src\ is touched.
# ASCII-only: Windows PowerShell 5.1 reads UTF-8 without BOM as ANSI and mangles non-ASCII.
param(
    [int]$TriggerAfterMs = 250,
    [int]$TotalMs = 1800
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class Reveal
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);

    public delegate bool EnumProc(IntPtr h, IntPtr lp);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L; public int T; public int R; public int B; }

    public static IntPtr FindChildOfProcess(IntPtr parent, uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr lp)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p == pid)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Run(IntPtr taskbar, IntPtr mine, int triggerAfterMs, int totalMs)
    {
        StringBuilder sb = new StringBuilder();
        Stopwatch sw = Stopwatch.StartNew();
        bool triggered = false;
        double lastRecord = -1;
        int lastTop = int.MinValue;
        int lastVis = -1;

        while (sw.Elapsed.TotalMilliseconds < totalMs)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            if (!triggered && now >= triggerAfterMs)
            {
                triggered = true;
                SetCursorPos(1280, 1599);   // touch the bottom edge to reveal the auto-hidden taskbar
            }

            RECT t;
            if (!GetWindowRect(taskbar, out t)) { continue; }

            int vis = -1;
            int mineTop = int.MinValue;
            if (mine != IntPtr.Zero)
            {
                RECT m;
                if (GetWindowRect(mine, out m)) { mineTop = m.T; }
                vis = IsWindowVisible(mine) ? 1 : 0;
            }

            bool changed = (t.T != lastTop) || (vis != lastVis);
            if (changed || now - lastRecord >= 2.0)
            {
                sb.Append(now.ToString("0.000")).Append('\t')
                  .Append(t.T).Append('\t')
                  .Append(t.B - t.T).Append('\t')
                  .Append(vis).Append('\t')
                  .Append(mineTop == int.MinValue ? "-" : mineTop.ToString()).Append('\n');
                lastRecord = now;
                lastTop = t.T;
                lastVis = vis;
            }

            Thread.SpinWait(120);
        }

        return sb.ToString();
    }
}
'@

[void][Reveal]::SetProcessDpiAwarenessContext([IntPtr](-4))

$taskbar = [Reveal]::FindWindow('Shell_TrayWnd', $null)
if ($taskbar -eq [IntPtr]::Zero) { Write-Output 'Shell_TrayWnd not found'; exit 1 }

$ryu = Get-Process RyuBar -ErrorAction SilentlyContinue | Select-Object -First 1
$mine = [IntPtr]::Zero
if ($ryu) {
    $mine = [Reveal]::FindChildOfProcess($taskbar, [uint32]$ryu.Id)
    Write-Output ("RyuBar pid={0} host hwnd=0x{1:X}" -f $ryu.Id, $mine.ToInt64())
} else {
    Write-Output 'RyuBar is not running (measuring the taskbar alone)'
}

# make sure it starts hidden
[void][Reveal]::SetCursorPos(1280, 400)
Start-Sleep -Seconds 3

Write-Output ('sampling for {0} ms, reveal triggered at {1} ms...' -f $TotalMs, $TriggerAfterMs)
$raw = [Reveal]::Run($taskbar, $mine, $TriggerAfterMs, $TotalMs)

$rows = @()
foreach ($line in ($raw -split "`n")) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $f = $line -split "`t"
    $rows += [pscustomobject]@{
        Ms = [double]$f[0]
        Top = [int]$f[1]
        Height = [int]$f[2]
        Visible = [int]$f[3]
        MineTop = $f[4]
    }
}

Write-Output ''
Write-Output '== taskbar top over time (only changes, plus every 2 ms) =='
$prev = $null
foreach ($r in $rows) {
    $mark = ''
    if ($null -ne $prev -and $r.Top -ne $prev) { $mark = ' <== moved' }
    Write-Output ("  {0,8:N3} ms   top={1,5}  h={2,3}  ourWindowVisible={3}  ourTop={4}{5}" -f $r.Ms, $r.Top, $r.Height, $r.Visible, $r.MineTop, $mark)
    $prev = $r.Top
}

# --- analysis -------------------------------------------------------------------
$first = $rows | Select-Object -First 1
$last = $rows | Select-Object -Last 1
$distinctTops = ($rows | Select-Object -ExpandProperty Top | Sort-Object -Unique)
$moves = @()
$prevTop = $null
foreach ($r in $rows) {
    if ($null -ne $prevTop -and $r.Top -ne $prevTop) { $moves += $r }
    $prevTop = $r.Top
}

Write-Output ''
Write-Output '== verdict =='
Write-Output ("  distinct taskbar tops observed : {0}" -f $distinctTops.Count)
Write-Output ("  top values                    : {0}" -f ($distinctTops -join ', '))

if ($moves.Count -gt 0) {
    $animStart = $moves[0].Ms
    $animEnd = $moves[-1].Ms
    Write-Output ("  first move at                  : {0:N3} ms (top {1})" -f $animStart, $moves[0].Top)
    Write-Output ("  last  move at                  : {0:N3} ms (top {1})" -f $animEnd, $moves[-1].Top)
    Write-Output ("  motion duration                : {0:N1} ms" -f ($animEnd - $animStart))
    Write-Output ("  motion frames (distinct steps) : {0}" -f $moves.Count)
    if ($distinctTops.Count -ge 3 -and ($animEnd - $animStart) -gt 20) {
        Write-Output '  => CONTINUOUS: the shell animates the taskbar window position over time.'
        Write-Output '     A child window therefore rides along, so removing SW_HIDE should make it slide out too.'
    } else {
        Write-Output '  => INSTANT: the taskbar jumped between positions.'
        Write-Output '     The child cannot ride a jump; an own slide compensation would be required.'
    }
} else {
    Write-Output '  no taskbar movement observed - is the taskbar set to auto-hide?'
}

Write-Output ''
Write-Output ("  our window visible at start    : {0}" -f $first.Visible)
Write-Output ("  our window visible at end      : {0}" -f $last.Visible)
$flips = @()
$prevVis = $null
foreach ($r in $rows) {
    if ($null -ne $prevVis -and $r.Visible -ne $prevVis) { $flips += $r }
    $prevVis = $r.Visible
}
foreach ($f in $flips) {
    Write-Output ("  visibility flip at {0:N3} ms -> visible={1} (ourTop={2})" -f $f.Ms, $f.Visible, $f.MineTop)
}
if ($flips.Count -le 1 -and $last.Visible -eq 1) {
    Write-Output '  => our host appears in ONE step (no intermediate frames) = the current pop.'
}
