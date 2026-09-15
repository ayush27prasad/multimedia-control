using System.Globalization;
using System.Text.RegularExpressions;
using PhoneBridge.Core.Protocol;

namespace PhoneBridge.Adb;

/// <summary>
/// Wi-Fi control over ADB. The PC is the authority for this slice because the phone app
/// literally cannot do any of it (DECISIONS.md D5/D6).
///
/// Scope is deliberately narrow. Every command used here is in WifiShellCommand's
/// NON_PRIVILEGED_COMMANDS allowlist, so it is guaranteed for the shell uid:
///   status, list-networks, list-scan-results, start-scan, set-wifi-enabled
///
/// There is NO connect. <c>cmd wifi connect-network</c> requires the passphrase on every
/// call and cannot activate a saved network by id - there is no select-network or
/// enable-network in cmd wifi on Android 11/12/13. The user taps the network on the phone.
///
/// Output formats vary across Android releases and OEM skins, so every parser here is
/// tolerant and every method preserves the raw text for diagnostics. Run
/// scripts/spike-wifi.ps1 against the actual device before trusting any of it.
/// </summary>
public sealed partial class WifiController
{
    private readonly AdbClient _adb;

    public event Action<string>? Log;

    /// <summary>Raw output of the last poll, for the diagnostics pane and bug reports.</summary>
    public string LastRawStatus { get; private set; } = "";
    public string LastRawNetworks { get; private set; } = "";
    public string LastRawScan { get; private set; } = "";

    public WifiController(AdbClient adb) => _adb = adb;

    // ---- read --------------------------------------------------------------

    public async Task<WifiState> ReadStateAsync(CancellationToken ct = default)
    {
        var status = await _adb.ShellAsync(ct, "cmd", "wifi", "status").ConfigureAwait(false);
        LastRawStatus = status.Combined;

        if (status.IsPermissionDenied)
        {
            return new WifiState
            {
                LastError = "adb shell was denied access to 'cmd wifi status'. On MIUI, check " +
                            "Developer options -> USB debugging (Security settings).",
                LastUpdated = Now()
            };
        }
        if (status.IsUnknownCommand)
        {
            return new WifiState
            {
                LastError = "'cmd wifi' is not available on this build. Run scripts/spike-wifi.ps1.",
                LastUpdated = Now()
            };
        }
        if (!status.Ok)
        {
            return new WifiState { LastError = status.Combined, LastUpdated = Now() };
        }

        var text = status.StdOut;
        var enabled = !text.Contains("Wifi is disabled", StringComparison.OrdinalIgnoreCase);

        var saved = await ListSavedNetworksAsync(ct).ConfigureAwait(false);
        var scan = await ListScanResultsAsync(ct).ConfigureAwait(false);

        // Only parse connection detail when the radio is on; the status dump omits the
        // WifiInfo block entirely when it is off, and a stale SSID would be misleading.
        string? ssid = null, bssid = null;
        int? rssi = null, linkSpeed = null, freq = null, netId = null;
        var connected = false;

        if (enabled)
        {
            ssid = Match(SsidRegex(), text);
            if (ssid is "<unknown ssid>" or "<none>") ssid = null;

            bssid = Match(BssidRegex(), text);
            rssi = MatchInt(RssiRegex(), text);
            linkSpeed = MatchInt(LinkSpeedRegex(), text);
            freq = MatchInt(FrequencyRegex(), text);
            netId = MatchInt(NetworkIdRegex(), text);

            // "Supplicant state: COMPLETED" is the reliable marker; an SSID can appear
            // while still associating.
            connected = text.Contains("COMPLETED", StringComparison.OrdinalIgnoreCase) && ssid is not null;
        }

        return new WifiState
        {
            Enabled = enabled,
            Connected = connected,
            Ssid = ssid,
            Bssid = bssid,
            Rssi = rssi,
            LinkSpeedMbps = linkSpeed,
            FrequencyMhz = freq,
            NetworkId = netId,
            SavedNetworks = saved,
            ScanResults = scan,
            LastUpdated = Now()
        };
    }

    /// <summary>
    /// Parses `cmd wifi list-networks`, whose columns are "%-12d %-32s %-4s"
    /// (networkId, SSID, security type). SSIDs may contain spaces, so the id is taken
    /// from the front and the security type from the back, leaving the middle as the SSID.
    /// </summary>
    public async Task<List<SavedNetwork>> ListSavedNetworksAsync(CancellationToken ct = default)
    {
        var result = await _adb.ShellAsync(ct, "cmd", "wifi", "list-networks").ConfigureAwait(false);
        LastRawNetworks = result.Combined;

        var list = new List<SavedNetwork>();
        if (!result.Ok || result.IsPermissionDenied) return list;

        foreach (var raw in result.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            // Header line, and the "No networks" message.
            if (line.Contains("Network Id", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("No ", StringComparison.OrdinalIgnoreCase)) continue;

            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            if (!int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;

            var security = tokens[^1].TrimEnd('^');
            var ssid = string.Join(' ', tokens[1..^1]).Trim();
            if (ssid.Length == 0)
            {
                // Two columns only: an SSID with no security column, or an empty SSID.
                ssid = tokens.Length == 2 ? tokens[1] : "";
                security = "";
            }

            list.Add(new SavedNetwork { NetworkId = id, Ssid = ssid, Security = security });
        }

        // Android 13 Mainline prints one row per security type of the same networkId
        // (wpa2-psk plus wpa3-sae^, or open plus owe^). Collapse so the UI lists each
        // saved network once.
        return list
            .GroupBy(n => n.NetworkId)
            .Select(g =>
            {
                var first = g.First();
                var securities = g.Select(n => n.Security).Where(s => s.Length > 0).Distinct();
                return first with { Security = string.Join('/', securities) };
            })
            .ToList();
    }

    /// <summary>
    /// Parses `cmd wifi list-scan-results`. The format comes from ScanResultUtil and is
    /// less stable than list-networks, so this extracts by pattern rather than by column.
    /// </summary>
    public async Task<List<ScanResultEntry>> ListScanResultsAsync(CancellationToken ct = default)
    {
        var result = await _adb.ShellAsync(ct, "cmd", "wifi", "list-scan-results").ConfigureAwait(false);
        LastRawScan = result.Combined;

        var list = new List<ScanResultEntry>();
        if (!result.Ok || result.IsPermissionDenied) return list;

        foreach (var raw in result.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("No scan results", StringComparison.OrdinalIgnoreCase)) break;
            if (line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase)) continue;

            var m = ScanLineRegex().Match(line);
            if (!m.Success) continue;

            var ssid = m.Groups["ssid"].Value.Trim();
            if (ssid.Length == 0) continue;   // hidden network

            list.Add(new ScanResultEntry
            {
                Ssid = ssid,
                FrequencyMhz = int.TryParse(m.Groups["freq"].Value, out var f) ? f : 0,
                Rssi = int.TryParse(m.Groups["rssi"].Value, out var r) ? r : 0,
                Security = SecurityFromFlags(m.Groups["flags"].Value)
            });
        }

        // Same SSID on multiple bands collapses to its strongest sighting - the user
        // cares about the network, not each radio.
        return list
            .GroupBy(e => e.Ssid)
            .Select(g => g.MaxBy(e => e.Rssi)!)
            .OrderByDescending(e => e.Rssi)
            .ToList();
    }

    // ---- write -------------------------------------------------------------

    /// <summary>
    /// Toggles the radio (FR9).
    ///
    /// Exit code 0 is NOT proof of success: setWifiEnabled silently returns false when
    /// airplane mode is on or a SoftAP is active. The caller must re-read state and
    /// compare, which is what <see cref="SetEnabledAndVerifyAsync"/> does.
    /// </summary>
    public async Task<AdbResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var arg = enabled ? "enabled" : "disabled";
        var result = await _adb.ShellAsync(ct, "cmd", "wifi", "set-wifi-enabled", arg).ConfigureAwait(false);

        if (result.Ok && !result.IsUnknownCommand) return result;

        // Legacy fallback. Same permission basis (com.android.shell holds NETWORK_SETTINGS)
        // but an older, non-Mainline code path.
        Log?.Invoke($"set-wifi-enabled unavailable ({result.Combined}); falling back to svc wifi");
        return await _adb.ShellAsync(ct, "svc", "wifi", enabled ? "enable" : "disable").ConfigureAwait(false);
    }

    /// <summary>Toggles, then confirms the radio actually moved. Returns null on success.</summary>
    public async Task<string?> SetEnabledAndVerifyAsync(bool enabled, CancellationToken ct = default)
    {
        var result = await SetEnabledAsync(enabled, ct).ConfigureAwait(false);
        if (result.IsPermissionDenied)
            return "adb was denied permission to change the Wi-Fi radio.";
        if (!result.Ok)
            return $"toggle failed: {result.Combined}";

        // The radio takes a moment to settle; poll rather than sleeping a fixed worst case.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(400, ct).ConfigureAwait(false);
            var state = await ReadStateAsync(ct).ConfigureAwait(false);
            if (state.Enabled == enabled) return null;
        }

        return enabled
            ? "Wi-Fi did not switch on. Airplane mode may be enabled."
            : "Wi-Fi did not switch off. A hotspot may be active.";
    }

    /// <summary>
    /// Triggers a scan. Results are not immediate - poll list-scan-results afterwards.
    /// Android throttles foreground scans to 4 per 2 minutes.
    /// </summary>
    public async Task<AdbResult> StartScanAsync(CancellationToken ct = default) =>
        await _adb.ShellAsync(ct, "cmd", "wifi", "start-scan").ConfigureAwait(false);

    /// <summary>
    /// Disables scan throttling. Optional, and it persists on the device after we exit,
    /// so it is opt-in rather than applied automatically at startup.
    /// </summary>
    public Task<AdbResult> DisableScanThrottlingAsync(CancellationToken ct = default) =>
        _adb.ShellAsync(ct, "settings", "put", "global", "wifi_scan_throttle_enabled", "0");

    // ---- parsing helpers ---------------------------------------------------

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? Match(Regex regex, string text)
    {
        var m = regex.Match(text);
        return m.Success ? m.Groups[1].Value.Trim().Trim('"') : null;
    }

    private static int? MatchInt(Regex regex, string text)
    {
        var m = regex.Match(text);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    private static string SecurityFromFlags(string flags) => flags switch
    {
        _ when flags.Contains("SAE", StringComparison.OrdinalIgnoreCase) ||
               flags.Contains("WPA3", StringComparison.OrdinalIgnoreCase) => "wpa3",
        _ when flags.Contains("RSN", StringComparison.OrdinalIgnoreCase) ||
               flags.Contains("WPA2", StringComparison.OrdinalIgnoreCase) => "wpa2",
        _ when flags.Contains("WPA", StringComparison.OrdinalIgnoreCase) => "wpa",
        _ when flags.Contains("WEP", StringComparison.OrdinalIgnoreCase) => "wep",
        _ when flags.Contains("OWE", StringComparison.OrdinalIgnoreCase) => "owe",
        _ => "open"
    };

    [GeneratedRegex(@"SSID:\s*""?([^"",\n]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex SsidRegex();

    [GeneratedRegex(@"BSSID:\s*([0-9a-f:]{17})", RegexOptions.IgnoreCase)]
    private static partial Regex BssidRegex();

    [GeneratedRegex(@"RSSI:\s*(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RssiRegex();

    [GeneratedRegex(@"Link speed:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkSpeedRegex();

    [GeneratedRegex(@"Frequency:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FrequencyRegex();

    [GeneratedRegex(@"Net ID:\s*(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkIdRegex();

    /// <summary>
    /// BSSID, frequency, RSSI, optional age, SSID, then flags in brackets. The SSID is
    /// non-greedy up to the first bracketed flag group, which is what allows spaces in it.
    ///
    /// Android 13 / MIUI 14 prints RSSI as <c>-78(0:-78)</c> (per-chain in parentheses)
    /// with no space before the '('. AOSP 12 parsers that expected a bare integer miss
    /// every row; the optional group is from the spike against this device.
    /// </summary>
    [GeneratedRegex(@"^(?<bssid>[0-9a-f:]{17})\s+(?<freq>\d+)\s+(?<rssi>-?\d+)(?:\([^)]*\))?\s+(?:[\d.]+\s+)?(?<ssid>.*?)\s*(?<flags>\[.*\])?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ScanLineRegex();
}
