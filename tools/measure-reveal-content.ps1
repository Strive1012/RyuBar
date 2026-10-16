# Track a taskbar *icon* (the clock) as pixels during the reveal.
# The taskbar window rect jumps and its surface is fully transparent on this machine, so the only
# thing the eye can follow is the taskbar CONTENT. This measures whether that content animates.
# Read-only.  ASCII-only for Windows PowerShell 5.1.
param(
    [int]$Column = 2480,
    [int]$TriggerAfterMs = 250,
    [int]$TotalMs = 1000,
    [int]$Brightness = 140
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class Content
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    /// Topmost y in the column whose pixel is bright enough to be taskbar content (white text/icon).
    public static int FindContentTop(IntPtr dc, int x, int yStart, int yEnd, int brightness)
    {
        for (int y = yStart; y <= yEnd; y++)
        {
            uint c = GetPixel(dc, x, y);
            if (c == 0xFFFFFFFF) { continue; }
            int r = (int)(c & 0xFF), g = (int)((c >> 8) & 0xFF), b = (int)((c >> 16) & 0xFF);
            int max = Math.Max(r, Math.Max(g, b));
            if (max >= brightness) { return y; }
        }
        return -1;
    }

    public static string Run(int x, int yStart, int yEnd, int brightness, int triggerAfterMs, int totalMs)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        StringBuilder sb = new StringBuilder();
        Stopwatch sw = Stopwatch.StartNew();
        bool triggered = false;
        int last = int.MinValue;
        double lastRecord = -1;

        while (sw.Elapsed.TotalMilliseconds < totalMs)
        {
            double now = sw.Elapsed.TotalMilliseconds;
            if (!triggered && now >= triggerAfterMs)
            {
                triggered = true;
                SetCursorPos(1280, 1599);
            }

            int top = FindContentTop(dc, x, yStart, yEnd, brightness);
            if (top != last || now - lastRecord >= 5.0)
            {
                sb.Append(now.ToString("0.00")).Append('\t').Append(top).Append('\n');
                lastRecord = now;
                last = top;
            }
            Thread.SpinWait(20);
        }

        ReleaseDC(IntPtr.Zero, dc);
        return sb.ToString();
    }
}
'@

[void][Content]::SetProcessDpiAwarenessContext([IntPtr](-4))

[void][Content]::SetCursorPos(1280, 400)
Start-Sleep -Seconds 3
Write-Output ("tracking column x={0}, y {1}..{2}, brightness>={3}; reveal triggered at {4} ms" -f $Column, 1400, 1599, $Brightness, $TriggerAfterMs)
$raw = [Content]::Run($Column, 1400, 1599, $Brightness, $TriggerAfterMs, $TotalMs)

$rows = @()
foreach ($line in ($raw -split "`n")) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $f = $line -split "`t"
    $rows += [pscustomobject]@{ Ms = [double]$f[0]; Top = [int]$f[1] }
}

Write-Output ''
Write-Output '== topmost bright pixel in the column (taskbar content) =='
$prev = $null
$moves = @()
foreach ($r in $rows) {
    $mark = ''
    if ($null -ne $prev -and $r.Top -ne $prev) {
        $mark = ' <== moved'
        $moves += $r
    }
    Write-Output ("  {0,8:N2} ms   y = {1}{2}" -f $r.Ms, $r.Top, $mark)
    $prev = $r.Top
}

Write-Output ''
Write-Output '== verdict =='
$distinct = (($rows | Select-Object -ExpandProperty Top | Sort-Object -Unique))
Write-Output ("  distinct positions : {0}   ({1})" -f $distinct.Count, (($distinct | Select-Object -First 12) -join ', '))
Write-Output ("  transitions        : {0}" -f $moves.Count)
if ($moves.Count -ge 2) {
    $d = $moves[-1].Ms - $moves[0].Ms
    Write-Output ("  first -> last move : {0:N2} ms -> {1:N2} ms  (span {2:N1} ms)" -f $moves[0].Ms, $moves[-1].Ms, $d)
}
if ($moves.Count -ge 4 -and (($moves[-1].Ms - $moves[0].Ms) -gt 30)) {
    Write-Output '  => the taskbar CONTENT animates: it slides over tens of milliseconds.'
} elseif ($moves.Count -ge 1) {
    Write-Output '  => the content mostly jumps (at most one or two steps).'
} else {
    Write-Output '  => no content movement observed in this column.'
}
