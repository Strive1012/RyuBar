# Patch-based reveal measurement: mean luminance of small regions, sampled ~3 ms apart,
# for two trigger modes (cursor jump vs injected multi-step mouse sweep).
#
# Instrument notes (learned the hard way):
#   * GetPixel costs ~4 ms per call -> unusable for a fast curve. BitBlt of a small patch is ~1 ms.
#   * Single probe pixels are unreliable (the centre of the Start logo is the gap between the four
#     squares). Mean luminance over a patch is robust to glyph gaps.
#
# Read-only; nothing under src\ is touched.  ASCII-only for Windows PowerShell 5.1.
param(
    [ValidateSet('jump', 'sweep', 'hide', 'both')]
    [string]$Mode = 'both',
    [int]$TriggerAfterMs = 400,
    [int]$TotalMs = 1500,
    [int]$SweepSteps = 24,
    [int]$SweepMs = 220,
    # Isolation test: sample immediately without parking the cursor first, so a fade that happens
    # for another reason (e.g. application startup) can be captured while the taskbar is idle.
    [switch]$NoHide
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class Patches
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    public const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
        public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    public struct Patch { public int X; public int Y; public int W; public int H; public string Name; }

    public static string Run(string mode, Patch[] patches, int triggerAfterMs, int totalMs, int sweepSteps, int sweepMs)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);

        int maxW = 1, maxH = 1;
        foreach (Patch p in patches) { if (p.W > maxW) { maxW = p.W; } if (p.H > maxH) { maxH = p.H; } }

        BITMAPINFO bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.bmiHeader.biWidth = maxW;
        bmi.bmiHeader.biHeight = -maxH;         // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;

        IntPtr bits;
        IntPtr dib = CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);

        StringBuilder sb = new StringBuilder();
        sb.Append("t");
        foreach (Patch p in patches) { sb.Append('\t').Append(p.Name); }
        sb.Append('\n');

        Stopwatch sw = Stopwatch.StartNew();
        bool triggered = false;
        double lastRecord = -1;
        double[] last = new double[patches.Length];
        for (int i = 0; i < last.Length; i++) { last[i] = -1; }

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
                else if (mode == "hide")
                {
                    SetCursorPos(1280, 400);
                }
                else
                {
                    int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
                    int delay = Math.Max(1, sweepMs / Math.Max(1, sweepSteps));
                    for (int i = 1; i <= sweepSteps; i++)
                    {
                        int x = 1280;
                        int y = 900 + (1599 - 900) * i / sweepSteps;
                        MoveAbsolute(x, y, w, h);
                        Thread.Sleep(delay);
                    }
                }
            }

            double[] means = new double[patches.Length];
            bool changed = false;
            for (int i = 0; i < patches.Length; i++)
            {
                Patch p = patches[i];
                bool ok = BitBlt(mem, 0, 0, p.W, p.H, screen, p.X, p.Y, SRCCOPY);
                if (!ok) { means[i] = -1; continue; }
                byte[] buffer = new byte[p.W * p.H * 4];
                Marshal.Copy(bits, buffer, 0, buffer.Length);
                double sum = 0;
                for (int k = 0; k < buffer.Length; k += 4)
                {
                    sum += (buffer[k] + buffer[k + 1] + buffer[k + 2]) / 3.0;
                }
                means[i] = sum / (p.W * p.H);
                if (last[i] >= 0 && Math.Abs(means[i] - last[i]) > 0.4) { changed = true; }
            }

            if (changed || last[0] < 0 || now - lastRecord >= 5.0)
            {
                sb.Append(now.ToString("0.0"));
                for (int i = 0; i < means.Length; i++) { sb.Append('\t').Append(means[i].ToString("0.0")); }
                sb.Append('\n');
                Array.Copy(means, last, means.Length);
                lastRecord = now;
            }
            Thread.SpinWait(10);
        }

        SelectObject(mem, old);
        DeleteObject(dib);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public static void MoveAbsolute(int x, int y, int screenW, int screenH)
    {
        INPUT[] input = new INPUT[1];
        input[0].type = INPUT_MOUSE;
        input[0].mi.dx = (int)Math.Round(x * 65535.0 / (screenW - 1));
        input[0].mi.dy = (int)Math.Round(y * 65535.0 / (screenH - 1));
        input[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@

[void][Patches]::SetProcessDpiAwarenessContext([IntPtr](-4))

$patchDefs = @(
    @{ X = 1115; Y = 1528; W = 70;  H = 70; Name = 'startbtn' },
    @{ X = 2426; Y = 1528; W = 118; H = 70; Name = 'clock' },
    @{ X = 700;  Y = 1530; W = 60;  H = 60; Name = 'bgGap' },
    @{ X = 100;  Y = 1537; W = 300; H = 54; Name = 'ourbar' }
)
$patches = New-Object 'Patches+Patch[]' $patchDefs.Count
for ($i = 0; $i -lt $patchDefs.Count; $i++) {
    $p = New-Object 'Patches+Patch'
    $p.X = $patchDefs[$i].X; $p.Y = $patchDefs[$i].Y; $p.W = $patchDefs[$i].W; $p.H = $patchDefs[$i].H; $p.Name = $patchDefs[$i].Name
    $patches[$i] = $p
}

function Invoke-Run([string]$runMode) {
    if (-not $NoHide) {
        if ($runMode -eq 'hide') {
            # 隐藏测试要先让任务栏露出来（触发时再把光标移走）
            [void][Patches]::SetCursorPos(1280, 400)
            Start-Sleep -Milliseconds 800
            [void][Patches]::SetCursorPos(1280, 1599)
            Start-Sleep -Seconds 3
        } else {
            [void][Patches]::SetCursorPos(1280, 700)
            Start-Sleep -Seconds 3
        }
    }
    Write-Output ''
    Write-Output ('============ mode={0}  trigger at {1} ms  noHide={2} ============' -f $runMode, $TriggerAfterMs, $NoHide)
    $raw = [Patches]::Run($runMode, $patches, $TriggerAfterMs, $TotalMs, $SweepSteps, $SweepMs)

    $rows = @()
    foreach ($line in ($raw -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $f = $line -split "`t"
        if ($f[0] -eq 't') { continue }
        $rows += [pscustomobject]@{ Ms = [double]$f[0]; Start = [double]$f[1]; Clock = [double]$f[2]; Bg = [double]$f[3]; Ours = [double]$f[4] }
    }

    $base = ($rows | Where-Object { $_.Ms -lt $TriggerAfterMs } | Select-Object -Last 1)
    $final = ($rows | Select-Object -Last 1)
    Write-Output ("baseline: start={0,6:N1} clock={1,6:N1} bg={2,6:N1} ourbar={3,6:N1}   (t={4:N0} ms)" -f $base.Start, $base.Clock, $base.Bg, $base.Ours, $base.Ms)
    Write-Output ("final   : start={0,6:N1} clock={1,6:N1} bg={2,6:N1} ourbar={3,6:N1}   (t={4:N0} ms)" -f $final.Start, $final.Clock, $final.Bg, $final.Ours, $final.Ms)

    Write-Output ''
    Write-Output '  t(ms)   start  clock   bgGap  ourbar      (mean luminance; a ramp = fade, one step = jump)'
    # With -NoHide there is no trigger, so nothing may be filtered out as "before the trigger".
    $cutoff = $TriggerAfterMs
    if ($NoHide -or $TriggerAfterMs -gt $TotalMs) { $cutoff = -1 }
    foreach ($r in $rows) {
        if ($r.Ms -lt ($cutoff - 30) -and $cutoff -ge 0) { continue }
        Write-Output ("  {0,6:N0}  {1,6:N1} {2,6:N1} {3,6:N1} {4,6:N1}" -f $r.Ms, $r.Start, $r.Clock, $r.Bg, $r.Ours)
    }

    Write-Output ''
    Write-Output '== verdict =='
    $names = @('startbtn', 'clock', 'bgGap', 'ourbar')
    $props = @('Start', 'Clock', 'Bg', 'Ours')
    for ($i = 0; $i -lt 4; $i++) {
        $b = $base.($props[$i]); $f0 = $final.($props[$i])
        $delta = $f0 - $b
        if ([Math]::Abs($delta) -lt 1.0) { Write-Output ("  {0,-9} no change (delta {1:N1}) - nothing drawn there" -f $names[$i], $delta); continue }
        $mid = ($b + $f0) / 2.0
        $inter = 0; $firstMs = $null; $doneMs = $null
        foreach ($r in $rows) {
            if ($r.Ms -lt $TriggerAfterMs) { continue }
            $v = $r.($props[$i])
            $p = ($v - $b) / $delta
            if ($p -gt 0.05 -and $null -eq $firstMs) { $firstMs = $r.Ms }
            if ($p -gt 0.95 -and $null -eq $doneMs) { $doneMs = $r.Ms }
            if ($p -gt 0.15 -and $p -lt 0.85) { $inter++ }
        }
        $shape = 'STEP (jump)'
        $dur = 0
        if ($null -ne $firstMs -and $null -ne $doneMs) { $dur = $doneMs - $firstMs }
        if ($inter -ge 2 -and $dur -ge 10) { $shape = 'RAMP (fade)' }
        if ($null -eq $firstMs) { Write-Output ("  {0,-9} delta {1,6:N1} but never crossed 5%" -f $names[$i], $delta); continue }
        Write-Output ("  {0,-9} delta {1,6:N1}  change {2,7:N0} ms -> {3,7:N0} ms  duration {4,5:N0} ms  intermediate samples {5,3}  shape {6}" -f `
            $names[$i], $delta, $firstMs, $doneMs, $dur, $inter, $shape)
    }
}

$modes = @()
if ($Mode -eq 'both') { $modes = @('jump', 'sweep') } else { $modes = @($Mode) }
foreach ($m in $modes) { Invoke-Run $m }
Write-Output ''
Write-Output 'done.'
