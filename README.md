# PhoneBridge

Use an Android phone as a Windows PC's webcam, microphone and Wi-Fi control surface —
**over USB only**. No wireless transport between phone and PC at any point.

Built for a Windows 11 desktop with no camera, mic or Wi-Fi, paired with a Redmi Note 10
Pro (Android 13 / MIUI 14).

## Start here

| Document | What it is |
|---|---|
| [`docs/HANDOFF-WINDOWS.md`](docs/HANDOFF-WINDOWS.md) | **Read first.** Setup, build, milestone gates, failure catalogue. |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | Why the architecture is what it is, with citations. Three PRD assumptions did not survive contact with the platform. |
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | The wire contract both apps implement. |
| [`docs/VCAM-SHM.md`](docs/VCAM-SHM.md) | Shared-memory layout between the app and the virtual camera. |
| [`docs/AGENT-BRIEF-M1.md`](docs/AGENT-BRIEF-M1.md) | Task brief for the first build session. |
| [`CLAUDE.md`](CLAUDE.md) | Standing rules for any agent working in this repo. |
| [`prd.md`](prd.md) | Original requirements. Superseded on three points — see DECISIONS D5, D6, D8. |

## How it works

```
ANDROID (Kotlin)                   USB cable                WINDOWS 11 (.NET 8 / WPF)
┌──────────────────────┐                          ┌───────────────────────────┐
│ BridgeService (FGS)  │                          │ PhoneBridge.App           │
│                      │                          │                           │
│ CameraX → Surface    │──ch 1: H.264 Annex-B────▶│ FFmpeg decode → preview   │
│   → MediaCodec H.264 │                          │        └────→ virtual cam │
│ AudioRecord          │──ch 2: PCM S16LE 48k────▶│ WASAPI → VB-Cable         │
│ ControlProtocol      │◀─ch 3: NDJSON, bidi─────▶│ ADB: cmd wifi             │
└──────────────────────┘                          └───────────────────────────┘
        ▲                                                       │
        └────────── adb reverse localabstract:phonebridge ◀──────┘
```

Media rides the ADB transport itself: app → AF_UNIX → adbd → USB bulk endpoints → adb
server → PC loopback. The device side never touches an IP stack — a stricter reading of
"wired only" than routing over USB tethering would be.

## What it does, and what it deliberately does not

**Does:** phone camera as a Windows webcam · phone mic as a Windows microphone · mirrored
control from either app, in sync · enumerate the phone's saved Wi-Fi networks · toggle the
Wi-Fi radio · live signal strength · dark UI on both ends.

**Deliberately does not:**

- **Connect to a saved Wi-Fi network.** `cmd wifi connect-network` requires the passphrase
  on every call and cannot activate a network by saved id — there is no `select-network`
  in `cmd wifi`. Rather than build a credential vault or depend on Shizuku, connecting is
  left to a tap on the phone. ([D5](docs/DECISIONS.md))
- **Toggle USB tethering.** `TETHER_PRIVILEGED` is signature-level and no shell command
  exposes it. The app deep-links to the settings page instead. ([D6](docs/DECISIONS.md))
- **Ship its own audio driver.** Windows has no user-mode way to create an audio capture
  endpoint. VB-Cable is required. ([D8](docs/DECISIONS.md))

## Requirements

- Windows 11 build 22000+ (for the virtual camera; everything else works on Windows 10)
- .NET 8 SDK · JDK 17 · Android SDK
- [VB-Cable](https://vb-audio.com/Cable/) for the virtual microphone
- Android 11+ phone with USB debugging enabled

## Status

M1 (control channel + Wi-Fi) through M4 (microphone) are implemented but **not yet
compiled or run** — see the handoff. The native virtual camera media source (M3) is a
documented build plan rather than code, for the reasons in
[its README](windows/native/PhoneBridge.VirtualCam/README.md).
