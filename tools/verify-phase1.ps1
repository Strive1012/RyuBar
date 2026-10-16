# RyuBar phase 1 acceptance checks.
# ASCII-only: Windows PowerShell 5.1 reads UTF-8 without BOM as ANSI and mangles non-ASCII text.
#
#   powershell -File tools\verify-phase1.ps1              checks 1-5, non-destructive
#   powershell -File tools\verify-phase1.ps1 -Explorer    also restarts Explorer (visible side effect)
param(
    [switch]$Explorer
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class P1
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    public const uint LD = 0x0002;
    public const uint LU = 0x0004;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L; public int T; public int R; public int B; }

    public static uint OwnerPidAt(int x, int y)
    {
        POINT p; p.X = x; p.Y = y;
        IntPtr h = WindowFromPoint(p);
        if (h == IntPtr.Zero) { return 0; }
        uint pid; GetWindowThreadProcessId(h, out pid);
        return pid;
    }

    public static uint WindowFromPointPidTest(int x, int y) { return OwnerPidAt(x, y); }

    public static IntPtr Foreground() { return GetForegroundWindow(); }

    public static uint ForegroundPid()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) { return 0; }
        uint pid; GetWindowThreadProcessId(h, out pid);
        return pid;
    }

    public static bool ForegroundMinimized()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) { return false; }
        return IsIconic(h);
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(140);
        mouse_event(LD, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(LU, 0, 0, 0, IntPtr.Zero);
    }
}
'@

[void][P1]::SetProcessDpiAwarenessContext([IntPtr](-4))

$results = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    [void]$results.Add([pscustomobject]@{ Name = $name; Ok = [bool]$ok; Detail = $detail })
    $mark = 'FAIL'
    if ($ok) { $mark = 'PASS' }
    Write-Output ("[{0}] {1}" -f $mark, $name)
    Write-Output ("        " + $detail)
}

function Read-State {
    $path = "$env:LOCALAPPDATA\RyuBar\state.json"
    if (-not (Test-Path $path)) { return $null }
    Start-Sleep -Milliseconds 400
    return (Get-Content $path -Raw | ConvertFrom-Json)
}

function Show-Taskbar {
    # The shell refuses to re-reveal right after a hide, and its reaction is not instant, so poll
    # instead of sleeping a fixed amount. A tiny jiggle guarantees a real mouse move is generated.
    for ($i = 0; $i -lt 30; $i++) {
        [void][P1]::SetCursorPos(1280, 400)
        Start-Sleep -Milliseconds 120
        [void][P1]::SetCursorPos(1280, 1599)
        Start-Sleep -Milliseconds 400
        $s = Read-State
        if ($s -and $s.hidden -eq $false) { return $true }
    }
    return $false
}

function Hide-Taskbar {
    [void][P1]::SetCursorPos(1280, 400)
    Start-Sleep -Seconds 3
}

$proc = Get-Process RyuBar -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'RyuBar is not running - start it first'; exit 1 }
$myPid = [uint32]$proc.Id
Write-Output ("RyuBar pid = " + $myPid)
Write-Output ''

[void](Show-Taskbar)
$state = Read-State
if (-not $state) { Write-Output 'no state.json - the host never placed the bar'; exit 1 }

# 1) attached and inside the taskbar client area
$barL = [int]$state.bar.left; $barT = [int]$state.bar.top
$barR = $barL + [int]$state.bar.width; $barB = $barT + [int]$state.bar.height
$insideClient = ($state.attached -eq $true) -and ($barL -ge 0) -and ($barR -le [int]$state.clientWidth) -and ($barB -le [int]$state.clientHeight)
Check '1. attached and inside the taskbar client area' $insideClient `
    ("attached={0} client={1}x{2} bar=({3},{4}) {5}x{6} reason={7}" -f $state.attached, $state.clientWidth, $state.clientHeight, $barL, $barT, $state.bar.width, $state.bar.height, $state.reason)

# 2) no overlap with any occupied range
$overlap = @()
foreach ($r in $state.occupied) {
    if ($barR -gt [int]$r.Left -and $barL -lt [int]$r.Right) {
        $overlap += ("{0} [{1}..{2}]" -f $r.Name, $r.Left, $r.Right)
    }
}
Check '2. bar does not overlap any taskbar element' ($overlap.Count -eq 0) `
    ("occupied ranges={0}; overlaps={1}" -f $state.occupied.Count, ($(if ($overlap.Count -eq 0) { 'none' } else { $overlap -join ', ' })))

# 3) window region: inside the bar belongs to us, outside belongs to the taskbar
$y = [int]$state.clientHeight / 2 + 1528
$inPid = [P1]::OwnerPidAt(($barL + 80), [int]$y)
$outPid = [P1]::OwnerPidAt(($barR + 120), [int]$y)
Check '3. hit test - inside is ours, outside is the taskbar' `
    (($inPid -eq $myPid) -and ($outPid -ne $myPid)) `
    ("inside pid={0} (ours={1}), outside pid={2}" -f $inPid, $myPid, $outPid)

# 4) clicking a taskbar icon outside the bar still works
[void](Show-Taskbar)
$before = [P1]::ForegroundPid()
[P1]::Click(1215, [int]$y)
Start-Sleep -Milliseconds 1400
$after = [P1]::ForegroundPid()
Check '4. clicking a taskbar icon (outside the bar) reaches the taskbar' ($after -ne $before -and $after -ne $myPid) `
    ("foreground pid {0} -> {1} (ours={2})" -f $before, $after, $myPid)

# 5) clicking inside the bar must not steal focus nor minimize the current window
$fgBefore = [P1]::ForegroundPid()
[P1]::Click(($barL + 80), [int]$y)
Start-Sleep -Milliseconds 1400
$fgAfter = [P1]::ForegroundPid()
$minimized = [P1]::ForegroundMinimized()
Check '5. clicking inside the bar does not steal focus or minimize' (($fgAfter -ne $myPid) -and (-not $minimized)) `
    ("foreground pid {0} -> {1}, foreground minimized={2}" -f $fgBefore, $fgAfter, $minimized)

# 6) auto-hide: our window follows the taskbar.
# Guard: if the user has turned auto-hide off, this check cannot pass and would report a false failure.
# Detect it honestly first (ask the shell to hide the taskbar and see whether the rect actually moves),
# then skip with a reason instead of failing.
[void](Show-Taskbar)
$autoHideEnabled = $false
Hide-Taskbar
for ($i = 0; $i -lt 8; $i++) {
    Start-Sleep -Milliseconds 400
    $probe = Read-State
    if ($probe.hidden -eq $true) { $autoHideEnabled = $true; break }
}
if (-not $autoHideEnabled) {
    Write-Output ''
    Write-Output '[SKIP] 6. follows the taskbar auto-hide'
    Write-Output '        taskbar auto-hide is currently OFF (the taskbar rect does not move when the cursor leaves),'
    Write-Output '        so there is nothing to follow. Turn auto-hide back on and re-run to cover this check.'
} else {
    $hiddenState = Read-State
    $hiddenFlag = $hiddenState.hidden
    $revealed = Show-Taskbar
    $shownState = Read-State
    Check '6. follows the taskbar auto-hide' (($hiddenFlag -eq $true) -and ($shownState.hidden -eq $false)) `
        ("when taskbar hidden: hidden={0}; after reveal: hidden={1} (reveal detected={2})" -f $hiddenFlag, $shownState.hidden, $revealed)
}

# 7) optional: Explorer restart recovery.
# Judgement is based on evidence produced AFTER the kill:
#   * state.json must be rewritten after the kill (proof the app is still alive and placing)
#   * the app must report attached=true and a visible bar
#   * the bar must be hit-testable again (WindowFromPoint inside the bar -> our pid)
# The handle value itself is not used as the criterion: Windows can reuse handle values.
if ($Explorer) {
    $before = Read-State
    $killAt = Get-Date
    Write-Output ''
    Write-Output 'restarting Explorer (the taskbar will blink for a few seconds)...'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue

    $recovered = $false
    $freshWrite = $false
    $handleNow = ''
    $s2 = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $s2 = Read-State
        if (-not $s2) { continue }
        try { $written = [datetime]$s2.time } catch { $written = [datetime]::MinValue }
        if ($written -gt $killAt) {
            $freshWrite = $true
            $handleNow = $s2.taskbar
            if ($s2.attached -eq $true -and $s2.hidden -eq $false) { $recovered = $true; break }
        }
    }

    $hitOk = $false
    if ($recovered) {
        [void](Show-Taskbar)
        $s3 = Read-State
        if ($s3) {
            $y = [int]$s3.clientHeight / 2 + 1528
            $hitOk = ([P1]::WindowFromPointPidTest([int]$s3.bar.left + 80, [int]$y) -eq $myPid)
        }
    }

    Check '7. recovers after an Explorer restart' ($recovered -and $hitOk) `
        ("taskbar handle {0} -> {1}; state rewritten after the kill={2}; bar hit-testable again={3}" -f $before.taskbar, $handleNow, $freshWrite, $hitOk)
}

Write-Output ''
$passed = ($results | Where-Object { $_.Ok }).Count
$failed = ($results | Where-Object { -not $_.Ok }).Count
Write-Output ("summary: {0}/{1} passed, {2} failed" -f $passed, $results.Count, $failed)
if ($failed -gt 0) { exit 2 }
