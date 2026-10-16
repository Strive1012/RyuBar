# Click a control on the RyuBar bar, using the layout the app itself reports.
#
#   powershell -File tools\bar-click.ps1 -Action layout          print the app-reported control rects
#   powershell -File tools\bar-click.ps1 -Action play            click play/pause
#   powershell -File tools\bar-click.ps1 -Action prev|next
#   powershell -File tools\bar-click.ps1 -Action seek -Ratio 0.6 click the progress bar at 60%
#
# Positions come from the "[layout] 控件矩形" line in app.log (bar-local pixels), not from guessing,
# so a click cannot land on the wrong control. ASCII-only for Windows PowerShell 5.1.
param(
    [ValidateSet('layout', 'play', 'prev', 'next', 'seek')]
    [string]$Action = 'layout',
    [double]$Ratio = 0.5
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public class BarClick2{
 [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c,string n);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,IntPtr e);
 [StructLayout(LayoutKind.Sequential)] public struct R{public int L,T,Ri,B;}
 public const uint LD=0x0002, LU=0x0004;
 public static void Click(int x,int y){ SetCursorPos(x,y); System.Threading.Thread.Sleep(120);
   mouse_event(LD,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(LU,0,0,0,IntPtr.Zero); }
}
'@
[void][BarClick2]::SetProcessDpiAwarenessContext([IntPtr](-4))

$stateFile = "$env:LOCALAPPDATA\RyuBar\state.json"
$logFile = "$env:LOCALAPPDATA\RyuBar\logs\ryubar.log"

# reveal the taskbar (the shell refuses to re-reveal right after hiding, so poll)
$revealed = $false
for ($i = 0; $i -lt 24; $i++) {
    [void][BarClick2]::SetCursorPos(1280, 400)
    Start-Sleep -Milliseconds 100
    [void][BarClick2]::SetCursorPos(1280, 1599)
    Start-Sleep -Milliseconds 500
    $state = Get-Content $stateFile -Raw | ConvertFrom-Json
    if ($state.hidden -eq $false) { $revealed = $true; break }
}
if (-not $revealed) { Write-Output 'the taskbar did not reveal; aborting'; exit 1 }
Start-Sleep -Milliseconds 800
$state = Get-Content $stateFile -Raw | ConvertFrom-Json

# Control rects, as reported by the app for the current bar width.
# Match on ASCII structure only: a Chinese pattern in this file would be mangled because Windows
# PowerShell 5.1 reads UTF-8 without BOM as ANSI.
$line = (Get-Content $logFile | Select-String -Pattern 'prev=\d+\.\.\d+\s+play=\d+\.\.\d+\s+next=\d+\.\.\d+' | Select-Object -Last 1).Line
if (-not $line) { Write-Output 'no layout line found in app.log yet; is the new build running?'; exit 1 }
$layout = $line.Substring($line.IndexOf('|') + 1).Trim()
Write-Output ("app-reported layout: " + $layout)

function Field([string]$name) {
    $m = [regex]::Match($layout, "$name=(\d+)\.\.(\d+)")
    if (-not $m.Success) { return $null }
    return @([int]$m.Groups[1].Value, [int]$m.Groups[2].Value)
}

if ($Action -eq 'layout') { exit 0 }

$range = $null
$label = $Action
switch ($Action) {
    'play' { $range = Field 'play' }
    'prev' { $range = Field 'prev' }
    'next' { $range = Field 'next' }
    'seek' {
        $p = Field 'progress'
        if ($p) {
            $range = @([int]($p[0] + ($p[1] - $p[0]) * $Ratio), [int]($p[0] + ($p[1] - $p[0]) * $Ratio))
            $label = "progress $Ratio"
        }
    }
}
if (-not $range) { Write-Output ("could not read '$Action' from the layout line"); exit 1 }

$localX = [int](($range[0] + $range[1]) / 2)
$tb = [BarClick2]::FindWindow('Shell_TrayWnd', $null)
$r = New-Object BarClick2+R
[void][BarClick2]::GetWindowRect($tb, [ref]$r)
$screenX = [int]$state.bar.left + $localX
$screenY = $r.T + [int]$state.bar.top + [int]($state.bar.height / 2)

Write-Output ("clicking {0} at bar-local x={1} -> screen ({2},{3})" -f $label, $localX, $screenX, $screenY)
[BarClick2]::Click($screenX, $screenY)
Start-Sleep -Milliseconds 1500
Write-Output 'clicked.'
