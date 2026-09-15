<#
.SYNOPSIS
  One-time Windows toolchain setup for PhoneBridge.
.DESCRIPTION
  The authoring machine (macOS) has no Java, no Android SDK and no .NET, so NOTHING in
  this repo has ever been compiled. This Windows box is the first and only build
  environment for BOTH apps. Run this once, then see docs/HANDOFF-WINDOWS.md.
.EXAMPLE
  .\setup-windows.ps1                 # install everything
  .\setup-windows.ps1 -SkipAndroid    # desktop app only
#>
[CmdletBinding()]
param(
    [switch] $SkipAndroid,
    [switch] $SkipDotnet,
    [switch] $SkipVbCable
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

function Test-Winget {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "winget not found. Install 'App Installer' from the Microsoft Store, then re-run."
    }
}

function Install-WingetPackage {
    param([string] $Id, [string] $Label)
    Write-Host "-> $Label" -ForegroundColor Cyan
    $installed = winget list --id $Id --exact --accept-source-agreements 2>$null | Out-String
    if ($installed -match [regex]::Escape($Id)) {
        Write-Host "   already installed" -ForegroundColor DarkGray
        return
    }
    winget install --id $Id --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { Write-Host "   winget returned $LASTEXITCODE - check manually" -ForegroundColor Yellow }
}

Write-Host ''
Write-Host 'PhoneBridge Windows setup' -ForegroundColor Cyan
Write-Host "repo: $repoRoot"
Write-Host ''

# --- Windows version gate ---------------------------------------------------
# MFCreateVirtualCamera requires Windows 11 build 22000+. There is no Win10 backport.
$build = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
Write-Host "Windows build: $build"
if ($build -lt 22000) {
    Write-Host 'WARNING: virtual camera (M3) requires Windows 11 build 22000+.' -ForegroundColor Red
    Write-Host '         M1/M2/M4 will still work. See DECISIONS.md D7.' -ForegroundColor Red
}

Test-Winget

if (-not $SkipDotnet) {
    Install-WingetPackage -Id 'Microsoft.DotNet.SDK.8' -Label '.NET 8 SDK (desktop app)'
}

# --- platform-tools (adb) ---------------------------------------------------
# Bundled into windows/tools/adb so the app ships a known adb version (D9).
$adbDir = Join-Path $repoRoot 'windows\tools\adb'
if (-not (Test-Path (Join-Path $adbDir 'adb.exe'))) {
    Write-Host '-> Android platform-tools (adb)' -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $adbDir | Out-Null
    $zip = Join-Path $env:TEMP 'platform-tools.zip'
    Invoke-WebRequest -Uri 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile $zip
    $tmp = Join-Path $env:TEMP "pt-$(Get-Random)"
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    Copy-Item (Join-Path $tmp 'platform-tools\*') $adbDir -Recurse -Force
    Remove-Item $zip, $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "   adb -> $adbDir" -ForegroundColor DarkGray
} else {
    Write-Host '-> Android platform-tools: already present' -ForegroundColor DarkGray
}

if (-not $SkipAndroid) {
    Install-WingetPackage -Id 'Microsoft.OpenJDK.17'   -Label 'JDK 17 (Gradle / Android build)'
    Install-WingetPackage -Id 'Google.AndroidStudio'   -Label 'Android Studio (Android SDK + emulator)'
    Write-Host ''
    Write-Host 'After Android Studio first-run finishes downloading the SDK, set ANDROID_HOME:' -ForegroundColor Yellow
    Write-Host '  setx ANDROID_HOME "$env:LOCALAPPDATA\Android\Sdk"' -ForegroundColor Yellow
    Write-Host '  (then open a NEW terminal so it takes effect)' -ForegroundColor Yellow
}

if (-not $SkipVbCable) {
    Write-Host ''
    Write-Host '-> VB-Cable (virtual microphone, M4)' -ForegroundColor Cyan
    Write-Host '   Required: Windows has NO user-mode way to create an audio capture endpoint.' -ForegroundColor DarkGray
    Write-Host '   See DECISIONS.md D8. Download and run the installer AS ADMINISTRATOR:' -ForegroundColor DarkGray
    Write-Host '     https://vb-audio.com/Cable/' -ForegroundColor Yellow
    Write-Host '   Then reboot. Verify with: .\scripts\check-vbcable.ps1' -ForegroundColor DarkGray
}

# --- report -----------------------------------------------------------------
Write-Host ''
Write-Host 'Verification' -ForegroundColor Cyan
foreach ($t in @(
    @{ N='dotnet'; C={ dotnet --version } },
    @{ N='java';   C={ java -version 2>&1 | Select-Object -First 1 } },
    @{ N='adb';    C={ & (Join-Path $adbDir 'adb.exe') version | Select-Object -First 1 } }
)) {
    try   { Write-Host ("  {0,-8} {1}" -f $t.N, (& $t.C)) -ForegroundColor Green }
    catch { Write-Host ("  {0,-8} NOT AVAILABLE (open a new terminal if just installed)" -f $t.N) -ForegroundColor Yellow }
}

Write-Host ''
Write-Host 'Next: enable Developer options + USB debugging on the phone, plug in, then run:' -ForegroundColor Cyan
Write-Host '  .\scripts\spike-wifi.ps1 -IncludeMutating' -ForegroundColor White
Write-Host ''
