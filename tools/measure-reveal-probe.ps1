# Probe-based reveal measurement: 1 ms RGB sampling at fixed pixels, two trigger modes.
#
# Why: the earlier test moved the cursor with a single SetCursorPos (a jump) and measured with GDI
# screen captures at ~46 ms. Both are suspect: the shell may reveal differently for a swept cursor,
# and GDI capture can miss DWM-composited animation. This script fixes both:
#   * trigger "sweep" injects a real multi-step mouse move through SendInput (absolute, 24 steps)
#   * measurement reads four fixed probe pixels every ~1 ms - no screen capture at all
#
# A probe pixel that ramps from the desktop colour to the taskbar colour = a fade.
# A probe pixel that switches in one sample = a jump.
#
# Read-only; no file under src\ is touched.  ASCII-only for Windows PowerShell 5.1.
param(
    [ValidateSet('jump', 'sweep', 'both')]
    [string]$Mode = 'both',
    [int]$TriggerAfterMs = 400,
    [int]$TotalMs = 1600,
    [int]$SweepSteps = 24,
    [int]$SweepMs = 220
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class Probe
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    /// Absolute move in virtual-desktop normalised coordinates (0..65535).
    public static void MoveAbsolute(int x, int y, int screenW, int screenH)
    {
        INPUT[] input = new INPUT[1];
        input[0].type = INPUT_MOUSE;
        input[0].mi.dx = (int)Math.Round(x * 65535.0 / (screenW - 1));
        input[0].mi.dy = (int)Math.Round(y * 65535.0 / (screenH - 1));
        input[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void Sweep(int fromX, int fromY, int toX, int toY, int steps, int totalMs)
    {
        int screenW = GetSystemMetrics(0);
        int screenH = GetSystemMetrics(1);
        int delay = Math.Max(1, totalMs / Math.Max(1, steps));
        for (int i = 1; i <= steps; i++)
        {
            int x = fromX + (toX - fromX) * i / steps;
            int y = fromY + (toY - fromY) * i / steps;
            MoveAbsolute(x, y, screenW, screenH);
            Thread.Sleep(delay);
        }
    }

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    public static string Run(string mode, int[] xs, int[] ys, int triggerAfterMs, int totalMs, int sweepSteps, int sweepMs)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        StringBuilder sb = new StringBuilder();
        sb.Append("t");
        for (int i = 0; i < xs.Length; i++) { sb.Append("\tp").Append(i); }
        sb.Append('\n');

        Stopwatch sw = Stopwatch.StartNew();
        bool triggered = false;
        double lastRecord = -1;
        uint[] last = new uint[xs.Length];
        for (int i = 0; i < last.Length; i++) { last[i] = 0xFFFFFFFF; }

        while (sw.Elapsed.TotalMilliseconds < totalMs)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            if (!triggered && now >= triggerAfterMs)
            {
                triggered = true;
                if (mode == "jump")
                {
                    SetCursorPos(1280, 1599);
                }
                else
                {
                    Sweep(1280, 900, 1280, 1599, sweepSteps, sweepMs);
                }
            }

            uint[] now2 = new uint[xs.Length];
            bool changed = false;
            for (int i = 0; i < xs.Length; i++)
            {
                now2[i] = GetPixel(dc, xs[i], ys[i]);
                if (now2[i] != last[i]) { changed = true; }
            }

            if (changed || now - lastRecord >= 2.0)
            {
                sb.Append(now.ToString("0.00"));
                for (int i = 0; i < xs.Length; i++) { sb.Append('\t').Append(now2[i] & 0xFFFFFF); }
                sb.Append('\n');
                Array.Copy(now2, last, now2.Length);
                lastRecord = now;
            }
            Thread.SpinWait(20);
        }

        ReleaseDC(IntPtr.Zero, dc);
        return sb.ToString();
    }
}
'@

[void][Probe]::SetProcessDpiAwarenessContext([IntPtr](-4))

# Probe layout: per run, four fixed pixels in the taskbar band.
$names = @('taskbar background (700,1564)', 'Start button (1149,1564)', 'clock (2490,1554)', 'our bar area (300,1564)')
$xs = @(700, 1149, 2490, 300)
$ys = @(1564, 1564, 1554, 1564)

function Invoke-Run([string]$runMode) {
    # park the cursor far away so the taskbar starts hidden
    [void][Probe]::SetCursorPos(1280, 700)
    Start-Sleep -Seconds 3
    Write-Output ''
    Write-Output ('============================================================')
    Write-Output ("run: mode={0}  trigger at {1} ms  sampling {2} ms" -f $runMode, $TriggerAfterMs, $TotalMs)
    Write-Output ('============================================================')
    $raw = [Probe]::Run($runMode, $xs, $ys, $TriggerAfterMs, $TotalMs, $SweepSteps, $SweepMs)

    $rows = @()
    foreach ($line in ($raw -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $f = $line -split "`t"
        if ($f[0] -eq 't') { continue }
        $rows += [pscustomobject]@{
            Ms = [double]$f[0]
            P0 = [int]$f[1]; P1 = [int]$f[2]; P2 = [int]$f[3]; P3 = [int]$f[4]
        }
    }

    # baseline = last sample before the trigger; final = last sample of the run
    $base = ($rows | Where-Object { $_.Ms -lt $TriggerAfterMs } | Select-Object -Last 1)
    $final = ($rows | Select-Object -Last 1)
    if (-not $base) { Write-Output 'no baseline samples'; return }

    Write-Output ''
    Write-Output ("baseline @ {0:N1} ms : p0=#{1:X6} p1=#{2:X6} p3=#{3:X6}" -f $base.Ms, $base.P0, $base.P1, $base.P3)
    Write-Output ("final    @ {0:N1} ms : p0=#{1:X6} p1=#{2:X6} p3=#{3:X6}" -f $final.Ms, $final.P0, $final.P1, $final.P3)

    Write-Output ''
    Write-Output '== progress toward the final value, per probe (0..1), sampled over time =='
    Write-Output '   t(ms)   bg    Start  clock  ourbar'
    foreach ($r in $rows) {
        if ($r.Ms -lt ($TriggerAfterMs - 20)) { continue }
        $vals = @()
        $pairs = @(@($r.P0, $base.P0, $final.P0), @($r.P1, $base.P1, $final.P1), @($r.P3, $base.P3, $final.P3))
        foreach ($p in $pairs) {
            $cur = $p[0]; $b = $p[1]; $f0 = $p[2]
            $b0 = ($b -band 0xFF); $b1 = (($b -shr 8) -band 0xFF); $b2 = (($b -shr 16) -band 0xFF)
            $f1 = ($f0 -band 0xFF); $f2 = (($f0 -shr 8) -band 0xFF); $f3 = (($f0 -shr 16) -band 0xFF)
            $c0 = ($cur -band 0xFF); $c1 = (($cur -shr 8) -band 0xFF); $c2 = (($cur -shr 16) -band 0xFF)
            # use the channel with the largest change
            $d0 = [Math]::Abs($f1 - $b0); $d1 = [Math]::Abs($f2 - $b1); $d2 = [Math]::Abs($f3 - $b2)
            $ch = 0
            if ($d1 -ge $d0 -and $d1 -ge $d2) { $ch = 1 }
            if ($d2 -ge $d0 -and $d2 -ge $d1) { $ch = 2 }
            $bch = @($b0, $b1, $b2)[$ch]; $fch = @($f1, $f2, $f3)[$ch]; $cch = @($c0, $c1, $c2)[$ch]
            $den = $fch - $bch
            if ($den -eq 0) { $vals += '  n/a ' }
            else {
                $p = ($cch - $bch) / [double]$den
                if ($p -lt 0) { $p = 0 }
                if ($p -gt 1) { $p = 1 }
                $vals += ('{0,5:N2}' -f $p)
            }
        }
        Write-Output ("  {0,7:N1}  {1}  {2}  {3}" -f $r.Ms, $vals[0], $vals[1], $vals[2])
    }

    Write-Output ''
    Write-Output '== verdict per probe =='
    for ($idx = 0; $idx -lt 3; $idx++) {
        $prop = @('P0', 'P1', 'P3')[$idx]
        $series = @()
        foreach ($r in $rows) {
            if ($r.Ms -lt $TriggerAfterMs) { continue }
            $series += [pscustomobject]@{ Ms = $r.Ms; V = $r.$prop }
        }
        $b = $base.$prop; $f0 = $final.$prop
        $b0 = ($b -band 0xFF); $b1 = (($b -shr 8) -band 0xFF); $b2 = (($b -shr 16) -band 0xFF)
        $f1 = ($f0 -band 0xFF); $f2 = (($f0 -shr 8) -band 0xFF); $f3 = (($f0 -shr 16) -band 0xFF)
        $d0 = [Math]::Abs($f1 - $b0); $d1 = [Math]::Abs($f2 - $b1); $d2 = [Math]::Abs($f3 - $b2)
        $sh = 0; if ($d1 -ge $d0) { $sh = 8 }; if ($d2 -ge $d0 -and $d2 -ge $d1) { $sh = 16 }
        $bch = ($b -shr $sh) -band 0xFF; $fch = ($f0 -shr $sh) -band 0xFF
        $den = $fch - $bch
        if ($den -eq 0) { Write-Output ("  {0,-30} no change (probe does not see the taskbar)" -f $names[$idx]); continue }

        $firstMs = $null; $lastMs = $null; $stepCount = 0
        $prevP = $null
        foreach ($s in $series) {
            $cch = ($s.V -shr $sh) -band 0xFF
            $p = ($cch - $bch) / [double]$den
            if ($p -gt 0.06 -and $null -eq $firstMs) { $firstMs = $s.Ms }
            if ($p -gt 0.94) { $lastMs = $s.Ms }
            if ($null -ne $prevP -and [Math]::Abs($p - $prevP) -gt 0.02) { $stepCount++ }
            $prevP = $p
        }
        if ($null -eq $firstMs) { Write-Output ("  {0,-30} never reached the final value" -f $names[$idx]); continue }
        $dur = 0.0
        if ($null -ne $lastMs) { $dur = $lastMs - $firstMs }
        $shape = 'RAMP (gradual)'
        if ($stepCount -le 2 -or $dur -le 4) { $shape = 'STEP (jump)' }
        Write-Output ("  {0,-30} start={1,7:N1} ms  done={2,7:N1} ms  duration={3,6:N1} ms  shape={4}" -f `
            $names[$idx], $firstMs, $lastMs, $dur, $shape)
    }
}

$modes = @()
if ($Mode -eq 'both') { $modes = @('jump', 'sweep') } else { $modes = @($Mode) }
foreach ($m in $modes) { Invoke-Run $m }

# leave the taskbar in the revealed state and the cursor where it was
Write-Output ''
Write-Output 'done.'
