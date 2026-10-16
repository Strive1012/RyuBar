# RyuBar build script. ASCII-only on purpose: Windows PowerShell 5.1 reads UTF-8 without BOM as
# ANSI, and non-ASCII text here has already broken this script once.
#
#   powershell -File build.ps1                build Release
#   powershell -File build.ps1 -Run           build and start
#   powershell -File build.ps1 -SelfContained single-file self-contained publish into dist\
#
# Builds are serial: WPF projects report duplicate errors when built in parallel.
param(
    [switch]$SelfContained,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\RyuBar.csproj'
$exePath = Join-Path $root 'src\bin\Release\net10.0-windows10.0.19041.0\RyuBar.exe'

# A running instance locks the output file: ask it to exit through its normal shutdown path first.
$running = Get-Process -Name 'RyuBar' -ErrorAction SilentlyContinue
if ($running) {
    if (Test-Path $exePath) {
        Write-Host 'An instance is running and locks the output file; asking it to exit...' -ForegroundColor Yellow
        & $exePath --exit | Out-Null
        Start-Sleep -Seconds 2
    }
    $still = Get-Process -Name 'RyuBar' -ErrorAction SilentlyContinue
    if ($still) {
        Write-Host 'Instance did not exit on request; forcing it.' -ForegroundColor Yellow
        $still | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }
}

Write-Host '== build (Release) ==' -ForegroundColor Cyan
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = $exePath

if ($SelfContained) {
    Write-Host '== publish single file (self-contained, win-x64) ==' -ForegroundColor Cyan
    $out = Join-Path $root 'dist'
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $out --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $exe = Join-Path $out 'RyuBar.exe'
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "published: $exe ($size MB)" -ForegroundColor Green
}

Write-Host "executable: $exe" -ForegroundColor Green

if ($Run) {
    Start-Process -FilePath $exe
    Write-Host "started. diagnose: RyuBar.exe --diag    stop: RyuBar.exe --exit" -ForegroundColor Green
}
