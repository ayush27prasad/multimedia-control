# Wi-Fi capability report

Generated: 2026-09-16 02:23:36

## Device

- `ro.build.version.incremental` = V14.0.1.0.TKFINXM
- `ro.build.version.release` = 13
- `ro.build.version.sdk` = 33
- `ro.miui.ui.version.name` = V140
- `ro.product.manufacturer` = Xiaomi
- `ro.product.model` = M2101K6P

## Summary

| Probe | Verdict | Note |
|---|---|---|
| wifi status | OK | Drives the wifi state slice |
| wifi list-networks | OK | FR7 saved-network enumeration |
| wifi list-scan-results | OK | In-range APs |
| wifi start-scan | OK | Refresh scan results |
| wifi help | NONZERO EXIT (255) | Full subcommand list for THIS build |
| wifi select-network | DENIED (needs root) | Expected ABSENT - basis of D5 |
| wifi enable-network | DENIED (needs root) | Expected ABSENT - basis of D5 |
| connectivity tethering | NONZERO EXIT (255) | Expected: no tethering subcommand (D6) |
| wifi set-wifi-enabled disabled | OK | FR9 radio toggle |
| set-wifi-enabled EFFECTIVE | OK | Exit code 0 is not proof; the state must actually move. |
| svc wifi (fallback) | OK | Legacy fallback path |

> Shipping surface fully available. Wi-Fi module can be built as specified in DECISIONS.md D5.

## Raw output

### wifi status

`adb shell cmd wifi status` -> **OK**

```
Wifi is enabled
Wifi scanning is always available
==== Primary ClientModeManager instance ====
Wifi is connected to "Airtel_dih"
WifiInfo: SSID: "Airtel_dih", BSSID: f4:27:56:89:0a:e9, MAC: ba:0f:6c:7f:b6:72, IP: /192.168.1.3, Security type: 2, Supplicant state: COMPLETED, Wi-Fi standard: 5, RSSI: -29, Link speed: 433Mbps, Tx Link speed: 433Mbps, Max Supported Tx Link speed: 433Mbps, Rx Link speed: 433Mbps, Max Supported Rx Link speed: 433Mbps, Frequency: 5240MHz, Net ID: 52, Metered hint: false, score: 60, isUsable: true, CarrierMerged: false, SubscriptionId: -1, IsPrimary: 1, Trusted: true, Restricted: false, Ephemeral: false, OEM paid: false, OEM private: false, OSU AP: false, FQDN: <none>, Provider friendly name: <none>, Requesting package name: <none>"Airtel_dih"wpa2-pskMLO Information: , AP MLD Address: <none>, AP MLO Link Id: <none>, AP MLO Affiliated links: <none>
successfulTxPackets: 4473
successfulTxPacketsPerSecond: 226.01210576374189
retriedTxPackets: 255
retriedTxPacketsPerSecond: 14.346390194653637
lostTxPackets: 0
lostTxPacketsPerSecond: 0.0
successfulRxPackets: 5086
successfulRxPacketsPerSecond: 231.70652942020752
```

### wifi list-networks

`adb shell cmd wifi list-networks` -> **OK**

```
Network Id      SSID                         Security type
0            !!Free_CAAN_WorldLink_Wi-Fi!!    open
0            !!Free_CAAN_WorldLink_Wi-Fi!!    owe^
1            !!Free_WorldLink_Wi-Fi!!         open
1            !!Free_WorldLink_Wi-Fi!!         owe^
2            001Abhi                          wpa2-psk
2            001Abhi                          wpa3-sae^
3            443789014                        wpa2-psk
3            443789014                        wpa3-sae^
4            Abhi                             wpa2-psk
4            Abhi                             wpa3-sae^
5            Ankit                            wpa2-psk
5            Ankit                            wpa3-sae^
6            AyushΓÇÖs iPhone                   wpa2-psk
6            AyushΓÇÖs iPhone                   wpa3-sae^
7            CVHOSTEL                         wpa2-psk
7            CVHOSTEL                         wpa3-sae^
8            Class-Room                       open
8            Class-Room                       owe^
9            DESKTOP-599BI0I 0723             wpa2-psk
9            DESKTOP-599BI0I 0723             wpa3-sae^
10           DoNotConnect                     wpa2-psk
10           DoNotConnect                     wpa3-sae^
11           Free_WorldLink_GIBL_Wi-Fi        open
11           Free_WorldLink_GIBL_Wi-Fi        owe^
12           Ganga Bishun Prasad & Sons 5G    wpa2-psk
12           Ganga Bishun Prasad & Sons 5G    wpa3-sae^
13           Ganga Bishun Prasad & Sons       wpa2-psk
13           Ganga Bishun Prasad & Sons       wpa3-sae^
14           JioNet@KIET_MGAR                 open
14           JioNet@KIET_MGAR                 owe^
15           JioPrivateNet                    wpa2-enterprise
15           JioPrivateNet                    wpa3 enterprise^
16           KIET                             open
16           KIET                             owe^
17           KIET                             wpa2-psk
17           KIET                             wpa3-sae^
18           MNCS Pvt.Ltd.1994                wpa2-psk
18           MNCS Pvt.Ltd.1994                wpa3-sae^
19           MT pashmina_2.4                  wpa2-psk
19           MT pashmina_2.4                  wpa3-sae^
20           Moonnight_5G                     wpa2-psk
20           Moonnight_5G                     wpa3-sae^
21           Nishant's Galaxy A30             wpa2-psk
21           Nishant's Galaxy A30             wpa3-sae^
22           OPPO Reno5 Pro 5G                wpa2-psk
22           OPPO Reno5 Pro 5G                wpa3-sae^
23           POCO X3                          wpa2-psk
23           POCO X3                          wpa3-sae^
24           PRASAD                           wpa2-psk
24           PRASAD                           wpa3-sae^
25           Pracs                            wpa2-psk
25           Pracs                            wpa3-sae^
26           Raghav parjapat                  wpa2-psk
26           Raghav parjapat                  wpa3-sae^
27           RailWire                         open
27           RailWire                         owe^
28           Raman                            wpa2-psk
28           Raman                            wpa3-sae^
29           Realme 9 Speed Edition           wpa2-psk
29           Realme 9 Speed Edition           wpa3-sae^
30           Redmi 9 Power                    open
30           Redmi 9 Power                    owe^
31           Redmi A9+                        wpa2-psk
31           Redmi A9+                        wpa3-sae^
32           Redmi Note 10 Pro                open
32           Redmi Note 10 Pro                owe^
33           Redmi Note 8 Pro                 wpa2-psk
33           Redmi Note 8 Pro                 wpa3-sae^
34           Redmi                            wpa2-psk
34           Redmi                            wpa3-sae^
35           Rishikesh                        open
35           Rishikesh                        owe^
36           Rishikesh                        wpa2-psk
36           Rishikesh                        wpa3-sae^
37           Rishikesh_4G                     wpa2-psk
37           Rishikesh_4G                     wpa3-sae^
38           Rishikesh_5G                     wpa2-psk
38           Rishikesh_5G                     wpa3-sae^
39           Skyworth_35813E                  wpa2-psk
39           Skyworth_35813E                  wpa3-sae^
40           T1-1504_5G                       wpa2-psk
40           T1-1504_5G                       wpa3-sae^
41           TAGORE                           open
41           TAGORE                           owe^
42           The_Everest_Hotel                open
42           The_Everest_Hotel                owe^
43           Vistara World                    open
43           Vistara World                    owe^
44           Xiaomi 11 Lite NE                wpa2-psk
44           Xiaomi 11 Lite NE                wpa3-sae^
45           Xiaomi Redmi 5 Plus              wpa2-psk
45           Xiaomi Redmi 5 Plus              wpa3-sae^
46           arabicacoffeeboudha              wpa2-psk
46           arabicacoffeeboudha              wpa3-sae^
47           aryan                            wpa2-psk
47           aryan                            wpa3-sae^
48           ayush                            wpa2-psk
48           ayush                            wpa3-sae^
49           its free                         wpa2-psk
49           its free                         wpa3-sae^
50           realme 7                         wpa2-psk
50           realme 7                         wpa3-sae^
51           realme 8                         wpa2-psk
51           realme 8                         wpa3-sae^
52           Airtel_dih                       wpa2-psk
52           Airtel_dih                       wpa3-sae^
```

### wifi list-scan-results

`adb shell cmd wifi list-scan-results` -> **OK**

```
BSSID              Frequency      RSSI           Age(sec)     SSID                                 Flags
  6c:4f:89:c2:20:df       2462    -78(0:-78)           21.236    Airtel_Airtel-Shambo-1068         [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS]
  f2:ed:b8:35:17:42       5220    -82(0:-82)           21.041                                      [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS][WPS]
  c0:2e:5f:ba:6b:78       5240    -67(0:-67)           20.999    Airtel_305                        [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS]
  36:e6:e6:e4:21:58       2462    -90(0:-90)           21.264    [LG_MicrowaveOven]2158            [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS]
  b4:3d:08:4c:e9:72       5805    -91(0:-91)           20.830    Airtel_Stars_Exotica              [WPA-PSK-TKIP+CCMP][WPA2-PSK-TKIP+CCMP][RSN-PSK-TKIP+CCMP][ESS]
  94:e6:86:ad:e9:25       2422    -88(0:-88)           21.594    DP_94E686ADE924                   [WPA2-PSK-CCMP+TKIP][RSN-PSK-CCMP+TKIP][ESS]
  7e:b0:de:06:67:9c       5220    -78(0:-78)           21.042    AirFiber-ooX2aa                   [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS][WPS]
  c8:f0:9e:f5:67:e9       2412    -79(0:-79)           21.669    DP_C8F09EF567E8                   [WPA2-PSK-CCMP+TKIP][RSN-PSK-CCMP+TKIP][ESS]
  44:95:3b:c7:53:71       5785    -84(0:-84)           20.887    Delulu_5G                         [WPA-PSK-CCMP][WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS][WPS]
  7e:b0:de:06:67:9b       2412    -60(0:-60)           21.664    AirFiber-ooX2aa                   [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS][WPS]
  44:95:3b:c7:53:70       2427    -64(0:-64)           21.534    Delulu_2.4G                       [WPA-PSK-CCMP][WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS][WPS]
  3c:52:a1:07:6d:a2       5765    -88(0:-88)           20.914    Sanvi Ishu_5G                     [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS][WPS]
  b4:3d:08:34:32:61       2422    -63(0:-63)           21.580    TheDarkNetRises_2.4G              [WPA-PSK-TKIP+CCMP][WPA2-PSK-TKIP+CCMP][RSN-PSK-TKIP+CCMP][ESS][WPS]
  f0:ed:b8:25:17:42       5220    -82(0:-82)           21.038    Airtel_vish_5519                  [WPA-PSK-TKIP+CCMP][WPA2-PSK-TKIP+CCMP][RSN-PSK-TKIP+CCMP][ESS][WPS]
  b4:3d:08:34:32:60       5180    -63(0:-63)           21.127    TheDarkNetRises_5G                [WPA-PSK-TKIP+CCMP][WPA2-PSK-TKIP+CCMP][RSN-PSK-TKIP+CCMP][ESS][WPS]
  3c:78:95:e6:aa:e2       2442    -81(0:-81)           21.411    Camp Nou                          [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS][WPS]
  12:10:81:e9:96:d8       2452    -85(0:-85)           21.350    Rajat_Sharma                      [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS][WPS]
  ce:2e:5f:ba:6b:78       5240    -67(0:-67)           21.002                                      [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS]
  f4:27:56:89:0a:e9       5240    -23(0:-23)           20.998    Airtel_dih                        [WPA-PSK-TKIP+CCMP][WPA2-PSK-TKIP+CCMP][RSN-PSK-TKIP+CCMP][ESS]
  42:78:95:e6:aa:e2       2442    -77(0:-77)           21.408                                      [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS]
  f4:27:56:89:0a:e8       2457    -28(0:-28)           21.284    Airtel_dih                        [WPA-PSK-TKIP+CCMP][WPA2-PSK-TKIP+CCMP][RSN-PSK-TKIP+CCMP][ESS]
  b6:3d:08:5c:e9:72       5805    -90(0:-90)           20.828    Airel_9886396860_5G               [WPA-PSK-TKIP+CCMP][WPA2-PSK-TKIP+CCMP][RSN-PSK-TKIP+CCMP][ESS]
  44:63:c2:3d:96:ff       5180    -87(0:-87)           21.109    Airtel_gour_0230                  [WPA2-PSK-CCMP][RSN-PSK-CCMP][ESS]
  40:22:d8:78:2f:cd       2437    -92(0:-92)           21.457    DP_4022D8782FCC                   [WPA2-PSK-CCMP+TKIP][RSN-PSK-CCMP+TKIP][ESS]
```

### wifi start-scan

`adb shell cmd wifi start-scan` -> **OK**

```

```

### wifi help

`adb shell cmd wifi help` -> **NONZERO EXIT (255)**

```
Wi-Fi (wifi) commands:
  help or -h
    Print this help text.
  get-country-code
    Gets country code as a two-letter string
  set-wifi-enabled enabled|disabled
    Enables/disables Wifi on this device.
  set-scan-always-available enabled|disabled
    Sets whether scanning should be available even when wifi is off.
  list-scan-results
    Lists the latest scan results
  start-scan
    Start a new scan
  list-networks
    Lists the saved networks
  forget-network <networkId>
    Remove the network mentioned by <networkId>
        - Use list-networks to retrieve <networkId> for the network
  status
    Current wifi status
  set-verbose-logging enabled|disabled 
    Set the verbose logging enabled or disabled
  is-verbose-logging
    Check whether verbose logging enabled or disabled
  start-restricting-auto-join-to-subscription-id subId
    temporarily disable all wifi networks except merged carrier networks with the given subId
  stop-restricting-auto-join-to-subscription-id
    Undo the effects of start-restricting-auto-join-to-subscription-id
  add-suggestion <ssid> open|owe|wpa2|wpa3 [<passphrase>] [-u] [-o] [-p] [-m]  [-s] [-d] [-b <bssid>] [-e] [-i] [-a <carrierId>] [-c <subscriptionId>]
    Add a network suggestion with provided params
    Use 'network-suggestions-set-user-approved com.android.shell yes' to approve suggestions added via shell (Needs root access)
    <ssid> - SSID of the network
    open|owe|wpa2|wpa3 - Security type of the network.
        - Use 'open' or 'owe' for networks with no passphrase
           - 'open' - Open networks (Most prevalent)
           - 'owe' - Enhanced open networks
        - Use 'wpa2' or 'wpa3' for networks with passphrase
           - 'wpa2' - WPA-2 PSK networks (Most prevalent)
           - 'wpa3' - WPA-3 PSK networks
    -u - Mark the suggestion untrusted.
    -o - Mark the suggestion oem paid.
    -p - Mark the suggestion oem private.
    -m - Mark the suggestion metered.
    -h - Mark the network hidden.
    -s - Share the suggestion with user.
    -d - Mark the suggestion autojoin disabled.
    -b <bssid> - Set specific BSSID.
    -r - Enable non_persistent randomization (disabled by default)
    -a - Mark the suggestion carrier merged
    -c <carrierId> - set carrier Id
    -i <subscriptionId> - set subscription Id, if -a is used, this must be set
  remove-suggestion <ssid> [-l]
    Remove a network suggestion with provided SSID of the network
    -l - Remove suggestion with lingering, if not set will disconnect immediately 
  remove-all-suggestions
    Removes all suggestions added via shell
  list-suggestions
    Lists the suggested networks added via shell
  set-coex-cell-channels [lte|nr <bandNumber 1-261> <downlinkFreqKhz or UNKNOWN: -1> <downlinkBandwidthKhz or UNKNOWN: 0> <uplinkFreqKhz or UNKNOWN: -1> <uplinkBandwidthKhz or UNKNOWN: 0>] ...
    Sets a list of zero or more cell channels to use for coex calculations. Actual device reported cell channels will be ignored until reset-coex-cell-channels is called.
  reset-coex-cell-channels
    Removes all cell channels set in set-coex-cell-channels and returns to listening on actual device reported cell channels
  get-coex-cell-channels
    Prints the cell channels being used for coex.
  set-connected-score <score>
    Set connected wifi network score (to choose between LTE & Wifi for default route).
    This turns off the active connected scorer (default or external).
    Only works while connected to a wifi network. This score will stay in effect until you call reset-connected-score or the device disconnects from the current network.
    <score> - Integer score should be in the range of 0 - 60
  reset-connected-score
    Turns on the default connected scorer.
    Note: Will clear any external scorer set.
  start-softap <ssid> (open|wpa2|wpa3|wpa3_transition|owe|owe_transition) <passphrase> [-b 2|5|6|any|bridged|bridged_2_5|bridged_2_6|bridged_5_6] [-x] [-f <int> [<int>]]
    Start softap with provided params
    Note that the shell command doesn't activate internet tethering. In some devices, internet sharing is possible when Wi-Fi STA is also enabled and isassociated to another AP with internet access.
    <ssid> - SSID of the network
    open|wpa2|wpa3|wpa3_transition|owe|owe_transition - Security type of the network.
        - Use 'open', 'owe', 'owe_transition' for networks with no passphrase
        - Use 'wpa2', 'wpa3', 'wpa3_transition' for networks with passphrase
    -b 2|5|6|any|bridged|bridged_2_5|bridged_2_6|bridged_5_6 - select the preferred bands.
        - Use '2' to select 2.4GHz band as the preferred band
        - Use '5' to select 5GHz band as the preferred band
        - Use '6' to select 6GHz band as the preferred band
        - Use 'any' to indicate no band preference
        - Use 'bridged' to indicate bridged AP which enables APs on both 2.4G + 5G
        - Use 'bridged_2_5' to indicate bridged AP which enables APs on both 2.4G + 5G
        - Use 'bridged_2_6' to indicate bridged AP which enables APs on both 2.4G + 6G
        - Use 'bridged_5_6' to indicate bridged AP which enables APs on both 5G + 6G
    Note: If the band option is not provided, 2.4GHz is the preferred band.
          The exact channel is auto-selected by FW unless overridden by force-softap-channel command or '-f <int> <int>' option
    -f <int> <int> - force exact channel frequency for operation channel
    Note: -f <int> <int> - must be the last option
          For example:
          Use '-f 2412' to enable single Soft Ap on 2412
          Use '-f 2412 5745' to enable bridged dual Soft Ap on 2412 and 5745
    -x - Specifies the SSID as hex digits instead of plain text (T and above)
  stop-softap
    Stop softap (hotspot)
  pmksa-flush <networkId>
        - Flush the local PMKSA cache associated with the network id. Use list-networks to retrieve <networkId> for the network
  reload-resources
    Reset the WiFi resources cache which will cause them to be reloaded next time they are accessed. Necessary if overlays are manually modified.
  launch-dialog-simple [-t <title>] [-m <message>] [-l <url> <url_start> <url_end>] [-y <positive_button_text>] [-n <negative_button_text>] [-x <neutral_button_text>] [-c <timeout_millis>]
    Launches a simple dialog and waits up to 15 seconds to print the response.
    -t - Title
    -m - Message
    -l - URL of the message, with the start and end index inside the message
    -y - Positive Button Text
    -n - Negative Button Text
    -x - Neutral Button Text
    -c - Optional timeout in milliseconds
  launch-dialog-p2p-invitation-sent <device_name> <pin> [-i <display_id>]
    Launches a P2P Invitation Sent dialog.
    <device_name> - Name of the device the invitation was sent to
    <pin> - PIN for the invited device to input
  launch-dialog-p2p-invitation-received <device_name> [-p] [-d <pin>] [-i <display_id>] [-c <timeout_millis>]
    Launches a P2P Invitation Received dialog and waits up to 15 seconds to print the response.
    <device_name> - Name of the device sending the invitation
    -p - Show PIN input
    -d - Display PIN <pin>
    -i - Display ID
    -c - Optional timeout in milliseconds
  query-interface <uid> <package_name> STA|AP|AWARE|DIRECT [-new]
    Query whether the specified could be created for the specified UID and package name, and if so - what other interfaces would be destroyed
    -new - query for a new interfaces (otherwise an existing interface is ok
  interface-priority-interactive-mode enable|disable|default
    Enable or disable asking the user when there's an interface priority conflict, |default| implies using the device default behavior.
  set-one-shot-screen-on-delay-ms <delayMs>
    set the delay for the next screen-on connectivity scan in milliseconds.
  set-ipreach-disconnect enabled|disabled
    Sets whether CMD_IP_REACHABILITY_LOST events should trigger disconnects.
  get-ipreach-disconnect
    Gets setting of CMD_IP_REACHABILITY_LOST events triggering disconnects.
```

### wifi select-network

`adb shell cmd wifi select-network 0` -> **DENIED (needs root)**

```
Exception occurred while executing 'select-network':
java.lang.SecurityException: Uid 2000 does not have access to select-network wifi command (or such command doesn't exist)
	at com.android.server.wifi.WifiShellCommand.onCommand(WifiShellCommand.java:432)
	at com.android.wifi.x.com.android.modules.utils.BasicShellCommandHandler.exec(BasicShellCommandHandler.java:97)
	at com.android.server.wifi.WifiServiceImpl.handleShellCommand(WifiServiceImpl.java:4763)
	at android.os.Binder.onShellCommand(Binder.java:1107)
	at android.os.Binder.shellCommand(Binder.java:1054)
	at android.os.Binder.onTransact(Binder.java:882)
	at android.net.wifi.IWifiManager$Stub.onTransact(IWifiManager.java:2112)
	at android.os.Binder.execTransactInternal(Binder.java:1290)
	at android.os.Binder.execTransact(Binder.java:1249)
```

### wifi enable-network

`adb shell cmd wifi enable-network 0` -> **DENIED (needs root)**

```
Exception occurred while executing 'enable-network':
java.lang.SecurityException: Uid 2000 does not have access to enable-network wifi command (or such command doesn't exist)
	at com.android.server.wifi.WifiShellCommand.onCommand(WifiShellCommand.java:432)
	at com.android.wifi.x.com.android.modules.utils.BasicShellCommandHandler.exec(BasicShellCommandHandler.java:97)
	at com.android.server.wifi.WifiServiceImpl.handleShellCommand(WifiServiceImpl.java:4763)
	at android.os.Binder.onShellCommand(Binder.java:1107)
	at android.os.Binder.shellCommand(Binder.java:1054)
	at android.os.Binder.onTransact(Binder.java:882)
	at android.net.wifi.IWifiManager$Stub.onTransact(IWifiManager.java:2112)
	at android.os.Binder.execTransactInternal(Binder.java:1290)
	at android.os.Binder.execTransact(Binder.java:1249)
```

### connectivity tethering

`adb shell cmd -w connectivity help` -> **NONZERO EXIT (255)**

```
Connectivity service commands:
  help
    Print this help text.
  airplane-mode [enable|disable]
    Turn airplane mode on or off.
  airplane-mode
    Get airplane mode.
  set-chain3-enabled [true|false]
    Enable or disable FIREWALL_CHAIN_OEM_DENY_3 for debugging.
  get-chain3-enabled
    Returns whether FIREWALL_CHAIN_OEM_DENY_3 is enabled.
  set-package-networking-enabled [true|false] [package name]
    Set the deny bit in FIREWALL_CHAIN_OEM_DENY_3 to package. This has
    no effect if the chain is disabled.
  get-package-networking-enabled [package name]
    Get the deny bit in FIREWALL_CHAIN_OEM_DENY_3 for package.
  set-background-networking-enabled-for-uid [uid] [true|false]
    Set the allow bit in FIREWALL_CHAIN_BACKGROUND for the given uid.
  get-background-networking-enabled-for-uid [uid]
    Get the allow bit in FIREWALL_CHAIN_BACKGROUND for the given uid.
```

### wifi set-wifi-enabled disabled

`adb shell cmd wifi set-wifi-enabled disabled` -> **OK**

```

```

### set-wifi-enabled EFFECTIVE

`adb shell (observed state change)` -> **OK**

```
before:
Wifi is enabled
Wifi scanning is always available
==== Primary ClientModeManager instance ====
Wifi is connected to "Airtel_dih"
WifiInfo: SSID: "Airtel_dih", BSSID: f4:27:56:89:0a:e9, MAC: ba:0f:6c:7f:b6:72, IP: /192.168.1.3, Security type: 2, Supplicant state: COMPLETED, Wi-Fi standard: 5, RSSI: -28, Link speed: 433Mbps, Tx Link speed: 433Mbps, Max Supported Tx Link speed: 433Mbps, Rx Link speed: 433Mbps, Max Supported Rx Link speed: 433Mbps, Frequency: 5240MHz, Net ID: 52, Metered hint: false, score: 60, isUsable: true, CarrierMerged: false, SubscriptionId: -1, IsPrimary: 1, Trusted: true, Restricted: false, Ephemeral: false, OEM paid: false, OEM private: false, OSU AP: false, FQDN: <none>, Provider friendly name: <none>, Requesting package name: <none>"Airtel_dih"wpa2-pskMLO Information: , AP MLD Address: <none>, AP MLO Link Id: <none>, AP MLO Affiliated links: <none>
successfulTxPackets: 4748
successfulTxPacketsPerSecond: 140.05626624584562
retriedTxPackets: 272
retriedTxPacketsPerSecond: 8.794554012955805
lostTxPackets: 0
lostTxPacketsPerSecond: 0.0
successfulRxPackets: 5336
successfulRxPacketsPerSecond: 136.884527251679

after:
Wifi is disabled
Wifi scanning is always available
```

### svc wifi (fallback)

`adb shell svc wifi enable` -> **OK**

```

```

