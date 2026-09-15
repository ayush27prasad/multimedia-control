<#
.SYNOPSIS
  M0 capability spike. Probes which `cmd wifi` subcommands actually work on THIS phone.

.DESCRIPTION
  Research says these commands sit in WifiShellCommand.NON_PRIVILEGED_COMMANDS and are
  therefore guaranteed for the `shell` user on AOSP 11/12. That was verified against
  AOSP 12 source. The target here is Android 13 / MIUI 14, which is:
    - a later release than the source that was read, and
    - a heavily skinned OEM ROM.

  So this script is AUTHORITATIVE and the docs are not. Run it before writing or
  debugging any Wi-Fi code, and paste the report into the handoff notes.

  Read-only: it changes nothing except optionally toggling the radio (-IncludeMutating),
  which it restores afterwards.

.EXAMPLE
  .\spike-wifi.ps1
  .\spike-wifi.ps1 -IncludeMutating -OutFile report.md
#>
[CmdletBinding()]
param(
    [string] $AdbPath,
    [string] $Serial,
    [switch] $IncludeMutating,
    [string] $OutFile = "wifi-capability-report.md"
)

$ErrorActionPreference = 'Stop'

function Resolve-Adb {
    param([string] $Explicit)
    if ($Explicit) {
        if (Test-Path $Explicit) { return (Resolve-Path $Explicit).Path }
        throw "adb not found at supplied path: $Explicit"
    }
    $bundled = Join-Path $PSScriptRoot '..\windows\tools\adb\adb.exe'
    if (Test-Path $bundled) { return (Resolve-Path $bundled).Path }
    $onPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "adb.exe not found. Pass -AdbPath, or install platform-tools (see scripts/setup-windows.ps1)."
}

$adb = Resolve-Adb -Explicit $AdbPath
$serialArgs = if ($Serial) { @('-s', $Serial) } else { @() }

# Deliberately NOT using ANDROID_ADB_SERVER_PORT here. The spike should talk to whatever
# adb server the developer already has running, so its results reflect the machine's real
# state rather than a private server the app will later use.

function Invoke-Adb {
    <# Returns a hashtable: ExitCode, StdOut, StdErr, Combined. Never throws on non-zero. #>
    param([string[]] $Arguments)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $adb
    foreach ($a in ($serialArgs + $Arguments)) { [void]$psi.ArgumentList.Add($a) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow  = $true

    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    if (-not $p.WaitForExit(15000)) { try { $p.Kill() } catch {}; $err += "`n<TIMED OUT after 15s>" }

    return @{
        ExitCode = $p.ExitCode
        StdOut   = $out.TrimEnd()
        StdErr   = $err.TrimEnd()
        Combined = (($out + "`n" + $err).Trim())
    }
}

$results = [System.Collections.Generic.List[object]]::new()

function Test-Capability {
    param(
        [string]   $Name,
        [string[]] $ShellArgs,
        [string]   $Note = ''
    )
    Write-Host "  probing: $Name ... " -NoNewline
    $r = Invoke-Adb (@('shell') + $ShellArgs)

    # A SecurityException is the documented rejection for commands outside the
    # NON_PRIVILEGED_COMMANDS allowlist. Treat it as a hard NO, distinct from a
    # command that ran and simply reported an error.
    $verdict =
        if     ($r.Combined -match 'SecurityException')                  { 'DENIED (needs root)' }
        elseif ($r.Combined -match 'Unknown command|unknown option')     { 'ABSENT on this build' }
        elseif ($r.Combined -match 'Exception|error:')                   { 'ERROR' }
        elseif ($r.ExitCode -ne 0)                                       { "NONZERO EXIT ($($r.ExitCode))" }
        else                                                             { 'OK' }

    $color = switch -Wildcard ($verdict) {
        'OK'        { 'Green' }
        'DENIED*'   { 'Red' }
        'ABSENT*'   { 'Red' }
        default     { 'Yellow' }
    }
    Write-Host $verdict -ForegroundColor $color

    $results.Add([pscustomobject]@{
        Name    = $Name
        Command = ($ShellArgs -join ' ')
        Verdict = $verdict
        Output  = $r.Combined
        Note    = $Note
    })
}

Write-Host ''
Write-Host 'PhoneBridge M0 — Wi-Fi capability spike' -ForegroundColor Cyan
Write-Host "adb: $adb"

# ---- device presence -------------------------------------------------------
$devices = Invoke-Adb @('devices', '-l')
Write-Host ''
Write-Host $devices.StdOut
$deviceLines = @($devices.StdOut -split "`n" | Select-Object -Skip 1 | Where-Object { $_ -match '\S' })
if ($deviceLines.Count -eq 0) {
    throw "No device. Check the cable, enable Developer options -> USB debugging, and accept the RSA prompt on the phone."
}
if ($deviceLines.Count -gt 1 -and -not $Serial) {
    throw "Multiple devices attached. Re-run with -Serial <serial>."
}
if ($deviceLines[0] -match 'unauthorized') {
    throw "Device is UNAUTHORIZED. Unlock the phone and tap 'Allow' on the USB debugging prompt."
}

# ---- device identity -------------------------------------------------------
Write-Host ''
Write-Host 'Device identity' -ForegroundColor Cyan
$props = @{}
foreach ($k in @('ro.product.model','ro.build.version.release','ro.build.version.sdk',
                 'ro.miui.ui.version.name','ro.build.version.incremental','ro.product.manufacturer')) {
    $props[$k] = (Invoke-Adb @('shell','getprop',$k)).StdOut
    Write-Host ("  {0,-34} {1}" -f $k, $props[$k])
}

# ---- the commands we intend to ship (D5) -----------------------------------
Write-Host ''
Write-Host 'Shipping surface (must all be OK)' -ForegroundColor Cyan
Test-Capability -Name 'wifi status'           -ShellArgs @('cmd','wifi','status')            -Note 'Drives the wifi state slice'
Test-Capability -Name 'wifi list-networks'    -ShellArgs @('cmd','wifi','list-networks')     -Note 'FR7 saved-network enumeration'
Test-Capability -Name 'wifi list-scan-results' -ShellArgs @('cmd','wifi','list-scan-results') -Note 'In-range APs'
Test-Capability -Name 'wifi start-scan'       -ShellArgs @('cmd','wifi','start-scan')        -Note 'Refresh scan results'

# ---- confirming what we deliberately excluded (D5) -------------------------
Write-Host ''
Write-Host 'Confirming the excluded surface really is unavailable' -ForegroundColor Cyan
Test-Capability -Name 'wifi help'             -ShellArgs @('cmd','wifi','help')              -Note 'Full subcommand list for THIS build'
Test-Capability -Name 'wifi select-network'   -ShellArgs @('cmd','wifi','select-network','0') -Note 'Expected ABSENT - basis of D5'
Test-Capability -Name 'wifi enable-network'   -ShellArgs @('cmd','wifi','enable-network','0') -Note 'Expected ABSENT - basis of D5'
Test-Capability -Name 'connectivity tethering' -ShellArgs @('cmd','-w','connectivity','help') -Note 'Expected: no tethering subcommand (D6)'

# ---- mutating probes -------------------------------------------------------
if ($IncludeMutating) {
    Write-Host ''
    Write-Host 'Mutating probes (radio toggle; original state restored)' -ForegroundColor Cyan

    $before = (Invoke-Adb @('shell','cmd','wifi','status')).Combined
    $wasEnabled = $before -notmatch 'Wifi is disabled'
    Write-Host "  radio currently: $(if ($wasEnabled) {'ENABLED'} else {'DISABLED'})"

    $target = if ($wasEnabled) { 'disabled' } else { 'enabled' }
    Test-Capability -Name "wifi set-wifi-enabled $target" -ShellArgs @('cmd','wifi','set-wifi-enabled',$target) -Note 'FR9 radio toggle'
    Start-Sleep -Seconds 2

    $after = (Invoke-Adb @('shell','cmd','wifi','status')).Combined
    $actuallyChanged = (($after -notmatch 'Wifi is disabled') -ne $wasEnabled)
    if ($actuallyChanged) {
        Write-Host '  radio state DID change -> set-wifi-enabled is effective' -ForegroundColor Green
    } else {
        Write-Host '  radio state did NOT change despite exit 0.' -ForegroundColor Red
        Write-Host '  Most likely: airplane mode is on, or a SoftAP/hotspot is active.' -ForegroundColor Red
    }
    $results.Add([pscustomobject]@{
        Name='set-wifi-enabled EFFECTIVE'; Command='(observed state change)'
        Verdict=$(if($actuallyChanged){'OK'}else{'INEFFECTIVE'}); Output="before:`n$before`n`nafter:`n$after"; Note='Exit code 0 is not proof; the state must actually move.'
    })

    # restore
    $restore = if ($wasEnabled) { 'enabled' } else { 'disabled' }
    [void](Invoke-Adb @('shell','cmd','wifi','set-wifi-enabled',$restore))
    Write-Host "  restored radio to $restore"

    Test-Capability -Name 'svc wifi (fallback)' -ShellArgs @('svc','wifi',$(if($wasEnabled){'enable'}else{'disable'})) -Note 'Legacy fallback path'
} else {
    Write-Host ''
    Write-Host 'Skipping mutating probes. Re-run with -IncludeMutating to test the radio toggle (FR9).' -ForegroundColor Yellow
}

# ---- report ----------------------------------------------------------------
$okCount     = @($results | Where-Object { $_.Verdict -eq 'OK' }).Count
$shipping    = @($results | Where-Object { $_.Name -in @('wifi status','wifi list-networks','wifi list-scan-results','wifi start-scan') })
$shippingBad = @($shipping | Where-Object { $_.Verdict -ne 'OK' })

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('# Wi-Fi capability report')
[void]$sb.AppendLine()
[void]$sb.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Device')
[void]$sb.AppendLine()
foreach ($k in $props.Keys | Sort-Object) { [void]$sb.AppendLine("- ``$k`` = $($props[$k])") }
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Summary')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Probe | Verdict | Note |')
[void]$sb.AppendLine('|---|---|---|')
foreach ($r in $results) { [void]$sb.AppendLine("| $($r.Name) | $($r.Verdict) | $($r.Note) |") }
[void]$sb.AppendLine()
if ($shippingBad.Count -gt 0) {
    [void]$sb.AppendLine('> **The shipping surface is NOT fully available on this device.**')
    [void]$sb.AppendLine('> Failing: ' + (($shippingBad | ForEach-Object { $_.Name }) -join ', '))
    [void]$sb.AppendLine('> Do not write Wi-Fi code against the assumed surface until this is resolved.')
    [void]$sb.AppendLine('> If these show DENIED, check MIUI Developer options -> "USB debugging (Security settings)",')
    [void]$sb.AppendLine('> which requires a Mi account sign-in. See docs/HANDOFF-WINDOWS.md.')
} else {
    [void]$sb.AppendLine('> Shipping surface fully available. Wi-Fi module can be built as specified in DECISIONS.md D5.')
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Raw output')
foreach ($r in $results) {
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("### $($r.Name)")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("``adb shell $($r.Command)`` -> **$($r.Verdict)**")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine($r.Output)
    [void]$sb.AppendLine('```')
}

$sb.ToString() | Set-Content -Path $OutFile -Encoding UTF8

Write-Host ''
Write-Host "$okCount/$($results.Count) probes OK. Report written to $OutFile" -ForegroundColor Cyan
if ($shippingBad.Count -gt 0) {
    Write-Host 'SHIPPING SURFACE INCOMPLETE - see report before writing Wi-Fi code.' -ForegroundColor Red
    exit 1
}
Write-Host 'Shipping surface confirmed.' -ForegroundColor Green
