<#
.SYNOPSIS
  Verify VB-Cable is installed and report the exact endpoint IDs PhoneBridge needs (D8).
#>
$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host 'VB-Cable endpoint check' -ForegroundColor Cyan
Write-Host ''

# Render endpoints are "playback" devices; capture are "recording".
# PhoneBridge WRITES to the render endpoint "CABLE Input"; the user SELECTS the capture
# endpoint "CABLE Output" as their microphone in Zoom/Teams.
$audio = Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -in @('AudioEndpoint','MEDIA') }
$cable = $audio | Where-Object { $_.Name -match 'CABLE' }

if (-not $cable) {
    Write-Host 'VB-Cable NOT found.' -ForegroundColor Red
    Write-Host ''
    Write-Host 'Install from https://vb-audio.com/Cable/ (run the installer as Administrator), then reboot.' -ForegroundColor Yellow
    Write-Host 'Windows provides no user-mode way to create a virtual microphone - see DECISIONS.md D8.' -ForegroundColor DarkGray
    exit 1
}

foreach ($c in $cable) { Write-Host "  $($c.Name)" -ForegroundColor Green }

$hasInput  = @($cable | Where-Object { $_.Name -match 'CABLE Input'  }).Count -gt 0
$hasOutput = @($cable | Where-Object { $_.Name -match 'CABLE Output' }).Count -gt 0

Write-Host ''
if ($hasInput -and $hasOutput) {
    Write-Host 'Both endpoints present.' -ForegroundColor Green
    Write-Host '  PhoneBridge renders into : CABLE Input  (a PLAYBACK device)'
    Write-Host '  You select as microphone : CABLE Output (a RECORDING device)'
    Write-Host ''
    Write-Host 'Set CABLE Input to 48000 Hz 16-bit stereo in Sound control panel -> Playback ->' -ForegroundColor DarkGray
    Write-Host 'CABLE Input -> Properties -> Advanced, to avoid a resample in the hot path.' -ForegroundColor DarkGray
} else {
    Write-Host 'Partial install - expected BOTH "CABLE Input" and "CABLE Output".' -ForegroundColor Yellow
    Write-Host 'Reinstall as Administrator and reboot.' -ForegroundColor Yellow
    exit 1
}
