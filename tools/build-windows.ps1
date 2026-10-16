<#
.SYNOPSIS
    AirGrab Windows packaging script: builds a self-contained single-file exe.

.DESCRIPTION
    Publishes windows/AirGrab.Windows as one self-contained AirGrab.exe.

    These switches are mandatory -- each one was learned the hard way:

      EnableMsixTooling
          Windows App SDK refuses to generate the embedded resources.pri without it,
          and PublishSingleFile fails outright:
          "PublishSingleFile requires EnableMsixTooling for embedded resources.pri generation"

      WindowsAppSDKSelfContained
          Bundles the Windows App SDK runtime so the target machine needs nothing installed.

      IncludeNativeLibrariesForSelfExtract
          onnxruntime.dll is a native library. Without this it stays outside the exe
          and the gesture pipeline cannot load at runtime.

      EnableCompressionInSingleFile
          Roughly halves the output size.

    The gesture models are EmbeddedResource, so they are merged into the assembly
    itself -- a plain Content item would be dropped by PublishSingleFile and the
    exe would be missing its models.

    Build-time environment variables are redirected into .workbuddy/ so that
    neither the user profile nor the global NuGet cache gets polluted.

.PARAMETER Output
    Output directory. Defaults to .workbuddy/publish/win-camera

.PARAMETER Configuration
    Build configuration. Defaults to Release

.EXAMPLE
    .\tools\build-windows.ps1
    .\tools\build-windows.ps1 -Output D:\dist
    .\tools\build-windows.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [string]$Output,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# ---- Locate repository root (this script lives in tools/) ----
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "windows\AirGrab.Windows\AirGrab.Windows.csproj"

if (-not (Test-Path $project)) {
    Write-Host "[ERROR] Project file not found: $project" -ForegroundColor Red
    exit 1
}

if (-not $Output) {
    $Output = Join-Path $repoRoot ".workbuddy\publish\win-camera"
}

# ---- Locate dotnet ----
#
# Picking dotnet off PATH is not enough: a machine can carry the .NET *runtime*
# at C:\Program Files\dotnet (no SDK) while the actual SDK sits in a user-level
# install under %USERPROFILE%\.dotnet that is not on PATH at all. Feeding the
# runtime-only host to `dotnet publish` fails with "No .NET SDKs were found".
#
# So probe every candidate and keep the first one that actually reports an SDK.
function Test-DotnetHasSdk([string]$exe) {
    try {
        $sdks = @(& $exe --list-sdks 2>$null)
        return $LASTEXITCODE -eq 0 -and $sdks.Count -gt 0
    } catch {
        return $false
    }
}

$candidates = New-Object System.Collections.ArrayList

if ($env:USERPROFILE) {
    [void]$candidates.Add((Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"))
}

$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath) {
    [void]$candidates.Add($onPath.Source)
}

[void]$candidates.Add("C:\Program Files\dotnet\dotnet.exe")

$dotnet = $null
foreach ($candidate in $candidates) {
    if ((Test-Path $candidate) -and (Test-DotnetHasSdk $candidate)) {
        $dotnet = $candidate
        break
    }
}

if (-not $dotnet) {
    Write-Host "[ERROR] No dotnet SDK found. Install .NET SDK 8 or later." -ForegroundColor Red
    Write-Host "        Checked:" -ForegroundColor DarkGray
    foreach ($candidate in $candidates) {
        Write-Host "          $candidate" -ForegroundColor DarkGray
    }
    exit 1
}

# ---- Isolate build environment ----
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".workbuddy\dotnet-home"
$env:NUGET_PACKAGES = Join-Path $repoRoot ".workbuddy\nuget"

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
New-Item -ItemType Directory -Force -Path $env:NUGET_PACKAGES | Out-Null

Write-Host ""
Write-Host "AirGrab / Windows single-file packaging" -ForegroundColor Cyan
Write-Host "  dotnet  : $dotnet"
Write-Host "  project : $project"
Write-Host "  output  : $Output"
Write-Host "  config  : $Configuration"
Write-Host ""

# ---- Release any handle on the output artifact ----
#
# A running AirGrab.exe holds a lock on itself, and the single-file bundle step
# overwrites that exact path -- repacking while the app is open (or while a
# previous launch is still shutting down) fails with:
#   MSB4018: The process cannot access the file ... because it is being used by another process
# So stop any instance and wipe the previous output before bundling.
$running = Get-Process -Name "AirGrab" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "AirGrab is running - stopping it so the output file can be replaced." -ForegroundColor Yellow
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

# Guard against a catastrophic -Output value such as "C:\" or "D:".
$isRootPath = $Output -match '^[A-Za-z]:\\?$'
if ((Test-Path $Output) -and (-not $isRootPath)) {
    Remove-Item -Recurse -Force $Output -ErrorAction SilentlyContinue
}

# ---- Publish ----
& $dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:EnableMsixTooling=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $Output

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "[FAILED] dotnet publish exited with code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# ---- Verify artifact ----
$exe = Join-Path $Output "AirGrab.exe"

if (-not (Test-Path $exe)) {
    Write-Host "[FAILED] Publish finished but AirGrab.exe is missing." -ForegroundColor Red
    exit 1
}

$item = Get-Item $exe
$sizeMb = [math]::Round($item.Length / 1MB, 1)

Write-Host ""
Write-Host "[OK] Packaging complete" -ForegroundColor Green
Write-Host "  file  : $($item.FullName)"
Write-Host "  size  : $sizeMb MB"
Write-Host "  time  : $($item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host ""
Write-Host "Self-contained single file -- the target machine needs neither .NET nor Windows App SDK." -ForegroundColor DarkGray
Write-Host ""
