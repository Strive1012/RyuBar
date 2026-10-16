# Burst-capture the taskbar area during the reveal and build one contact sheet.
# Answers two questions at once: does the taskbar CONTENT slide, and over how long?
# Read-only.  ASCII-only for Windows PowerShell 5.1.
param(
    [int]$X = 1050,
    [int]$Y = 1470,
    [int]$W = 700,
    [int]$H = 130,
    [int]$Frames = 8,
    [int]$TriggerAfterMs = 150,
    [int]$CycleMs = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'docs'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public class Burst{
 [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
}
'@
[void][Burst]::SetProcessDpiAwarenessContext([IntPtr](-4))

# start from the hidden state
[void][Burst]::SetCursorPos(1280, 400)
Start-Sleep -Seconds 3

$shots = @()
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$triggered = $false
for ($i = 0; $i -lt $Frames; $i++) {
    if (-not $triggered -and $sw.ElapsedMilliseconds -ge $TriggerAfterMs) {
        $triggered = $true
        [void][Burst]::SetCursorPos(1280, 1599)   # reveal
    }
    $ms = $sw.ElapsedMilliseconds
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size($W, $H)))
    $g.Dispose()
    $shots += [pscustomobject]@{ Ms = $ms; Bitmap = $bmp }
    Start-Sleep -Milliseconds $CycleMs
}

# contact sheet: one row per frame, timestamp burned in on the left
$pad = 120
$sheet = New-Object System.Drawing.Bitmap(($W + $pad), ($H * $shots.Count))
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.Clear([System.Drawing.Color]::FromArgb(255, 20, 20, 20))
$font = New-Object System.Drawing.Font('Consolas', 12)
$brush = [System.Drawing.Brushes]::White
for ($i = 0; $i -lt $shots.Count; $i++) {
    $sg.DrawString(("{0} ms" -f $shots[$i].Ms), $font, $brush, 4, ($i * $H + 4))
    $sg.DrawImage($shots[$i].Bitmap, $pad, ($i * $H))
    $sg.DrawLine([System.Drawing.Pens]::DimGray, 0, (($i + 1) * $H), ($W + $pad), (($i + 1) * $H))
}
$sg.Dispose()
$sheetPath = Join-Path $outDir 'phase1-reveal-burst.png'
$sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
foreach ($f in $shots) { $f.Bitmap.Dispose() }

Write-Output ("frames captured at (ms): " + (($shots | Select-Object -ExpandProperty Ms) -join ', '))
Write-Output ("contact sheet: " + $sheetPath)

# also measure the mean brightness of the taskbar band per frame: it rises as the taskbar comes in
Write-Output ''
Write-Output '== per-frame stats (taskbar band y=1530..1596 of the strip) =='
$bandTop = [Math]::Max(0, 1530 - $Y)
$bandBottom = [Math]::Min($H, 1596 - $Y)
$img = [System.Drawing.Bitmap]::FromFile($sheetPath)
for ($i = 0; $i -lt $shots.Count; $i++) {
    $sum = 0.0; $n = 0
    $tmp = New-Object System.Drawing.Bitmap($W, $H)
    $tg = [System.Drawing.Graphics]::FromImage($tmp)
    $tg.DrawImage($img, (New-Object System.Drawing.Rectangle(0,0,$W,$H)), (New-Object System.Drawing.Rectangle($pad, ($i*$H), $W, $H)), [System.Drawing.GraphicsUnit]::Pixel)
    $tg.Dispose()
    for ($yy = $bandTop; $yy -lt $bandBottom; $yy += 2) {
        for ($xx = 0; $xx -lt $W; $xx += 4) {
            $c = $tmp.GetPixel($xx, $yy)
            $sum += ($c.R + $c.G + $c.B) / 3.0
            $n++
        }
    }
    $tmp.Dispose()
    Write-Output ("  frame {0} ({1,4} ms)  band mean = {2,6:N1}" -f $i, $shots[$i].Ms, ($sum / [Math]::Max(1, $n)))
}
$img.Dispose()
