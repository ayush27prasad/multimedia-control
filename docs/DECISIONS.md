# Architecture Decisions

Each entry records a decision, the evidence behind it, and — where it applies — the PRD
assumption it overrides. **Read D4–D6 before touching the Wi-Fi or tethering code**, and
**D2 before touching the audio code**; they are the three places where the PRD specifies
something that is not achievable.

---

## D1 — Transport is `adb reverse` to an abstract `LocalServerSocket`, not RNDIS tethering

**Overrides PRD §7**, which routes media over the virtual network interface that USB
tethering exposes.

Rejected because tethering is a user-toggled setting an app cannot enable without system
permissions; the inbox Windows 11 driver is unreliable for both RNDIS and its CDC-NCM
successor; and it materialises a new network adapter that perturbs route metrics, trips
the firewall's new-network prompt, and confuses VPN software. It is also, functionally,
an IP network — which the PRD itself rules out.

Chosen instead: `adb reverse localabstract:…`. Bytes travel app → AF_UNIX → adbd → USB
bulk endpoints → adb server → PC loopback. The device side never touches an IP stack,
making this a *stricter* reading of "wired only" than the PRD's own proposal.

A normal installed app is permitted to create the abstract socket — AOSP sepolicy has
`allow adbd appdomain:unix_stream_socket connectto`, and every Chrome/WebView DevTools
session depends on it. scrcpy needs shell privileges for *screen capture*, not for the
socket; camera and mic are ordinary runtime permissions.

Reverse rather than forward because with `adb forward` the PC's `connect()` succeeds
whenever the rule exists, even with nothing listening on the device — it cannot signal
readiness. scrcpy documents this and works around it with a dummy byte. We keep forward
as a fallback with an explicit handshake timeout.

- <https://android.googlesource.com/platform/system/sepolicy/+/refs/heads/main/private/adbd.te>
- <https://github.com/Genymobile/scrcpy/blob/master/doc/develop.md>
- <https://developer.android.com/reference/android/net/LocalServerSocket>

## D2 — No WebRTC

**Overrides PRD §7, §8.**

Over a reliable, in-order, physically private pipe, WebRTC is pure cost: tens of MB of
native library, an SDP/ICE/DTLS handshake with no signalling server to carry it, and —
decisively — Google Congestion Control will read USB scheduling hiccups as congestion and
throttle the stream for no reason. Every problem its NACK/FEC/jitter-buffer machinery
solves is already solved by TCP over USB.

scrcpy, DroidCam, and Iriun all use a raw elementary stream over a socket. So do we.

## D3 — Codec: hardware H.264 + raw PCM

Bandwidth against a realistic ~100–320 Mb/s adb budget (measure yours with
`scripts/bench-throughput.ps1`; do not assume SuperSpeed — many phones enumerate the ADB
interface at USB 2.0 High-Speed regardless of the cable):

| Format | 1080p30 | Verdict |
|---|---|---|
| Raw NV12 | 746 Mb/s | Impossible |
| MJPEG ~10:1 | 36–72 Mb/s | Fits, but 10× the bitrate of H.264 for worse quality, and burns CPU |
| **H.264 hardware** | **8–20 Mb/s** | Large headroom, uses the otherwise-idle hardware encoder |

Audio is raw PCM S16LE 48 kHz stereo = 1.536 Mb/s, ~1.5% of budget. Chosen over Opus/AAC
for zero encoder latency, zero Windows decoder dependency, no codec-negotiation failure
modes, and because PCM sample counts *are* a clock for A/V sync.

Decode on Windows with `Sdcb.FFmpeg`: `AV_CODEC_FLAG_LOW_DELAY` and **slice-only**
threading. Never `FF_THREAD_FRAME` — frame threading adds N frames of latency, which is
the exact opposite of what we want.

## D4 — Windows UI is WPF on .NET 8, not WinUI 3

**Deviates from PRD §8**, which suggests "WPF or WinUI 3" and leans WinUI.

WinUI 3 needs the Windows App SDK and pushes toward MSIX packaging. Since none of this
can be compiled on the authoring machine and every build happens on the Windows box,
the iteration loop matters more than the design system: WPF gives a plain `dotnet build`
/ `dotnet run` with no SDK bootstrap and no packaging step. The dark, modern look the PRD
asks for (FR13) is delivered by an explicit theme in `Theme.xaml` rather than inherited
from a framework.

`MFCreateVirtualCamera` does not require packaging either (D7), so nothing downstream
needs WinUI.

## D5 — No Wi-Fi connect. Enumerate, status, and radio toggle only

**Overrides PRD FR8 and the §2 goal that rests on it.**

FR8 assumes `cmd wifi connect-network <SSID>` joins an already-saved network with no
credential entry. It does not. The real grammar is:

```
connect-network <ssid> open|owe|wpa2|wpa3 [<passphrase>] [-m] [-d] [-b <bssid>]
```

`WifiShellCommand` builds a **fresh** `WifiConfiguration` from those arguments and calls
`connect(config, -1, listener)` — `-1` meaning "no existing networkId". The passphrase is
required on every call. There is no `select-network`, `enable-network`, `reconnect`, or
`disconnect` in `cmd wifi` on Android 11/12/13; the only networkId-taking command is
`forget-network`. And `list-networks` returns networkId + SSID + security type, never
passphrases.

So "connect to a network the phone already knows, without handling credentials" has no
supported ADB path. Reading saved passphrases needs `getPrivilegedConfiguredNetworks()`
(signature|privileged) — root or Shizuku only.

Considered and rejected: a PC-side credential vault (works, but reintroduces exactly the
credential handling the PRD wanted to avoid) and a Shizuku tier (delivers FR8 as written,
but adds a dependency that MIUI's process killer treats badly and that needs re-arming
after every reboot).

**Decision: reduce scope.** The user taps the network on the phone. We ship
`list-networks`, `status`, `start-scan` / `list-scan-results`, and `set-wifi-enabled`.

All four are in `WifiShellCommand.NON_PRIVILEGED_COMMANDS`, an explicit allowlist checked
before a `Binder.getCallingUid() != ROOT_UID` rejection — so they are guaranteed for the
`shell` user. They work because `com.android.shell` declares `NETWORK_SETTINGS` and
`READ_WIFI_CREDENTIAL`. Wi-Fi is a Mainline module since Android 11, so this is
Google-shipped code that Xiaomi does not fork.

Prefer `cmd wifi set-wifi-enabled` over `svc wifi enable`: same allowlist, Mainline-
maintained, and the path Google's own tooling uses. `svc wifi` is the fallback. Never use
`settings put global wifi_on` — it desyncs the setting from the actual radio state.

- <https://android.googlesource.com/platform/packages/modules/Wifi/+/refs/tags/android-12.0.0_r1/service/java/com/android/server/wifi/WifiShellCommand.java>
- <https://github.com/aosp-mirror/platform_frameworks_base/blob/android12-release/packages/Shell/AndroidManifest.xml>

## D6 — Tethering is a guided deep link, not an automated toggle

**Overrides PRD §1.4.**

`startTethering(TETHERING_USB, …)` requires `TETHER_PRIVILEGED` — `signature|privileged`,
not grantable, not a runtime permission. No `cmd tethering` shell command exists on
Android 11/12/13. `svc usb setFunctions rndis` sets the USB gadget function but does not
reliably complete NAT/DHCP setup, and **would likely drop the ADB link the entire
transport depends on**.

The app deep-links into MIUI's tethering settings and detects the interface coming up. We
do not go near the USB gadget configuration.

## D7 — Virtual camera via `MFCreateVirtualCamera`, frames over shared memory

No code signing and no MSIX required — one elevated `regsvr32` into HKLM at install,
normal user at runtime. It is the only approach visible to *both* Media Foundation and
DirectShow consumers, so Teams, Zoom, Chrome, Discord, OBS, and the Windows Camera app
all see a single device. (A DirectShow filter would be simpler but invisible to every
WinRT/MF-only consumer, including the Camera app.)

The part Microsoft does not document: the media source is a **separate COM DLL that
Windows loads into `svchost.exe` running as Local Service** — it is not code in our
process. There is no `IMFVirtualCamera::WriteFrame`. Frames must cross via a
`Global\`-namespace shared-memory ring buffer whose security descriptor grants Local
Service read access.

Two consequences that cause most of the field failures:
- The DLL must live somewhere Local Service can read. Registering it from
  `C:\Users\…\source\repos\…` yields `E_ACCESSDENIED` or a silent black camera. Install to
  `%ProgramData%\PhoneBridge\` and register from there.
- Prefer a native (C++) or NativeAOT source. Hosting CoreCLR inside a system service host
  works but is a reliable source of strange field failures.

**Known risk to validate early:** Teams has an unresolved bug where the local preview
works but remote participants see black. It is called out in the VCamSample README and is
the one reported failure that could sink this approach. Test it at M3, not at M5.

- <https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera>
- <https://github.com/smourier/VCamSample> · <https://github.com/smourier/VCamNetSample>
- <https://github.com/bj-rn/VL.Video.VirtualCamera> (documents the shared-memory pattern)

## D8 — Virtual microphone via VB-Cable, not our own driver

**Overrides PRD §7**, which proposes "a virtual audio endpoint (WASAPI virtual device,
similar approach to VB-Cable)".

No such thing exists. There is no `MFCreateVirtualMicrophone`, no WASAPI endpoint-creation
call, and no user-mode audio driver framework — ACX, the newest WDK audio framework, is
explicitly KMDF-only *by design*, to avoid user/kernel transitions while streaming. Audio
endpoints are manufactured by MMDevAPI from kernel-streaming pins exposed by a PortCls/ACX
driver. No driver, no endpoint. VB-Cable *is* a signed kernel driver — that is the part
the PRD glossed over.

Shipping our own would need an EV certificate plus a Partner Center hardware account
(organisation identity, ~$250–500/yr) for Dev Portal signing. The free alternative is
test-signing, which costs the user Secure Boot, a BitLocker suspension, Memory Integrity,
a permanent desktop watermark, and working anti-cheat. Not acceptable.

**Decision:** require VB-Cable. Write PCM into the **"CABLE Input"** *render* endpoint via
WASAPI; the driver's internal wiring makes it emerge on **"CABLE Output"**, a genuine KS
capture endpoint that Zoom/Teams/Discord accept exactly like a physical mic. The user
selects "CABLE Output" as their microphone.

Match the endpoint by device ID or friendly name — never rely on the default device. VB-
Cable's format is set per-endpoint in the Sound control panel (commonly 48 kHz 16-bit
stereo); resample rather than assuming.

- <https://learn.microsoft.com/en-us/windows/win32/coreaudio/user-mode-audio-components>
- <https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/acx-audio-class-extensions-overview>
- <https://vb-audio.com/Cable/>

## D9 — Bundled adb on a private server port

Ship `adb.exe` (Apache-2.0, redistributable — scrcpy does the same) and set
`ANDROID_ADB_SERVER_PORT=5137`.

This is the highest-value robustness decision in the project. On the default port 5037, a
bundled adb of a different version than the user's Android Studio / scrcpy / Vysor will
repeatedly kill and restart the other's server — surfacing as random, unattributable
disconnects. A private port removes the whole class of failure.

`AdvancedSharpAdbClient` speaks the adb *server* protocol, which saves parsing `adb.exe`
stdout — but it does **not** remove the adb binary dependency. Only a libusb-level
reimplementation would, at the cost of implementing RSA auth and taking an exclusive USB
interface claim that breaks the moment the user opens Android Studio. Not worth it.

## D10 — Conflict resolution: authority model, not last-write-wins with a debounce

Resolves **PRD Open Question #3**. See `docs/PROTOCOL.md` §4.2.

A side that issues a command never updates its own state optimistically; it renders the
authoritative echo. Simultaneous conflicting commands resolve at the authority, so the two
UIs cannot disagree even transiently. The authority broadcasts full snapshots rather than
deltas, so reconnect resynchronises by construction with no replay log.
