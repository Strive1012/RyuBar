# Measure the taskbar edge *as pixels* during the reveal, not as a window rect.
# The window rect jumps (proven by tools\measure-reveal.ps1); this script answers whether the
# visible pixels slide smoothly, which is what the eye actually sees.
# Read-only: nothing under src\ is touched.  ASCII-only for Windows PowerShell 5.1.
param(
    [int]$Column = 800,
    [int]$TriggerAfterMs = 200,
    [int]$TotalMs = 900
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class Pixels
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);

    public static int Diff(uint a, uint b)
    {
        int ar = (int)(a & 0xFF), ag = (int)((a >> 8) & 0xFF), ab = (int)((a >> 16) & 0xFF);
        int br = (int)(b & 0xFF), bg = (int)((b >> 8) & 0xFF), bb = (int)((b >> 16) & 0xFF);
        int d = Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb);
        return d;
    }

    /// Finds the y where the pixel column stops matching the desktop reference above it.
    public static int FindEdge(IntPtr dc, int x, int yRef, int yEnd, int threshold)
    {
        uint reference = GetPixel(dc, x, yRef);
        for (int y = yRef + 6; y <= yEnd; y++)
        {
            uint c = GetPixel(dc, x, y);
            if (c == 0xFFFFFFFF) { continue; }
            if (Diff(c, reference) > threshold) { return y; }
        }
        return -1;
    }

    public static string Run(int x, int yRef, int yEnd, int threshold, int triggerAfterMs, int totalMs)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        StringBuilder sb = new StringBuilder();
        Stopwatch sw = Stopwatch.StartNew();
        bool triggered = false;
        int lastEdge = int.MinValue;
        double lastRecord = -1;

        while (sw.Elapsed.TotalMilliseconds < totalMs)
        {
            double now = sw.Elapsed.TotalMilliseconds;
            if (!triggered && now >= triggerAfterMs)
            {
                triggered = true;
                SetCursorPos(1280, 1599);
            }

            int edge = FindEdge(dc, x, yRef, yEnd, threshold);
            if (edge != lastEdge || now - lastRecord >= 4.0)
            {
                sb.Append(now.ToString("0.00")).Append('\t').Append(edge).Append('\n');
                lastRecord = now;
                lastEdge = edge;
            }
            Thread.SpinWait(30);
        }

        ReleaseDC(IntPtr.Zero, dc);
        return sb.ToString();
    }
}
'@

[void][Pixels]::SetProcessDpiAwarenessContext([IntPtr](-4))

# hide the taskbar first
[void][Pixels]::SetCursorPos(1280, 400)
Start-Sleep -Seconds 3

$ref = 1480     # above the taskbar even when it is shown (shown top = 1528)
$end = 1599
Write-Output ("scanning column x={0} from y={1} to y={2}, reveal triggered at {3} ms" -f $Column, $ref, $end, $TriggerAfterMs)
$raw = [Pixels]::Run($Column, $ref, $end, 60, $TriggerAfterMs, $TotalMs)

$rows = @()
foreach ($line in ($raw -split "`n")) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $f = $line -split "`t"
    $rows += [pscustomobject]@{ Ms = [double]$f[0]; Edge = [int]$f[1] }
}

Write-Output ''
Write-Output '== taskbar visible edge (the y where the taskbar starts) =='
$prevEdge = $null
$firstMoveMs = $null
$lastMoveMs = $null
$changes = 0
foreach ($r in $rows) {
    $mark = ''
    if ($null -ne $prevEdge -and $r.Edge -ne $prevEdge) {
        $mark = ' <== edge moved'
        $changes++
        if ($null -eq $firstMoveMs) { $firstMoveMs = $r.Ms }
        $lastMoveMs = $r.Ms
    }
    if ($mark -ne '' -or ($rows.Count -le 40)) {
        Write-Output ("  {0,8:N2} ms   edge y = {1}{2}" -f $r.Ms, $r.Edge, $mark)
    }
    $prevEdge = $r.Edge
}

Write-Output ''
Write-Output '== verdict =='
Write-Output ("  distinct edge positions : {0}" -f (($rows | Select-Object -ExpandProperty Edge | Sort-Object -Unique).Count))
Write-Output ("  edge transitions       : {0}" -f $changes)
if ($changes -gt 0) {
    Write-Output ("  first transition       : {0:N2} ms" -f $firstMoveMs)
    Write-Output ("  last  transition       : {0:N2} ms" -f $lastMoveMs)
    Write-Output ("  visual duration        : {0:N1} ms" -f ($lastMoveMs - $firstMoveMs))
}
if ($changes -ge 4 -and ($lastMoveMs - $firstMoveMs) -gt 40) {
    Write-Output '  => the PIXELS slide: the shell animates the taskbar visual over time.'
    Write-Output '     A separate child window is a different DWM surface, so it will NOT inherit that'
    Write-Output '     animation -> an own slide/fade on our side is required.'
} elseif ($changes -ge 2) {
    Write-Output '  => the pixels move in very few steps: mostly a jump with a short ease-out tail.'
} else {
    Write-Output '  => the pixels jump as well: there is no visible slide to follow.'
}
