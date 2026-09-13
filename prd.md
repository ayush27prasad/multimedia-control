# PRD: Phone-as-Peripheral Bridge (Android ↔ Windows, USB-Wired)

**Status:** Draft v3
**Scope:** Windows 11 desktop + Android mobile, connected via **USB cable only** (no LAN/Wi-Fi transport between phone and PC).

---

## 1. Problem Statement

The user's Windows PC has no built-in webcam, microphone, or Wi-Fi. The user's Android phone (Redmi Note 10 Pro) has a capable camera, mic, and Wi-Fi, and is connected to the PC via a **USB cable**.

Goal: build a native Android app + native Windows app that work together, over that USB connection, so the PC can:
1. Use the phone's front/rear camera as a virtual webcam.
2. Use the phone's microphone as a virtual microphone.
3. Trigger the phone to connect to one of its already-known Wi-Fi networks, controlled from either the desktop or the phone UI.
4. Route internet through the phone (USB tethering) when desired.

All of this happens over the wired USB link — no wireless transport between phone and PC at any point.

---

## 2. Goals

- Replace the current DroidCam-style workflow with a self-built, controllable equivalent.
- **Mirrored control surface**: camera source, mic source, mute state, and Wi-Fi connection can all be controlled from *either* the desktop app *or* the phone app — not desktop-only. State stays in sync both directions.
- Wi-Fi connection relies on the phone's own **already-saved/known networks** rather than the app managing new credentials — simplest reliable path, since Android already stores and can reconnect to known SSIDs itself.
- Modern, **dark-mode-first** UI on both apps.
- Low resource footprint on the desktop app — soft target, not a hard constraint.
- Prioritize peak camera/mic quality first; bandwidth is not a concern since USB has ample headroom.

## 3. Non-Goals (v1)

- Any wireless transport between phone and PC — this is a wired-only feature end to end.
- Connecting to *new* Wi-Fi networks the phone doesn't already know (out of scope — only known/saved networks are surfaced and triggered).

---

## 4. Users

Single user (you), technical, comfortable with ADB/developer options, Windows admin rights available.

---

## 5. Functional Requirements

### 5.1 Camera Module
- FR1: Android app exposes rear and front camera via CameraX; source switch controllable from **either** the phone app or the desktop app.
- FR2: Desktop app renders received video and exposes it as a **virtual camera device** (visible to Zoom/Teams/OBS/Discord as a normal webcam).
- FR3: Resolution/framerate selectable, defaulting to the phone's native capture quality — peak quality is the priority, not bandwidth conservation, since transport is wired USB.

### 5.2 Microphone Module
- FR4: Android app captures mic audio (front/rear mic if device exposes both — Redmi Note 10 Pro has dual mics for noise suppression; expose raw + processed if available).
- FR5: Desktop app exposes received audio as a **virtual microphone device** (WASAPI), selectable in any app's input settings.
- FR6: Mute/unmute togglable from **either** end; state syncs both directions in real time.

### 5.3 Wi-Fi Control Module
- FR7: Both apps can enumerate the phone's **already-known/saved Wi-Fi networks** (via ADB: `adb shell cmd wifi list-networks` or equivalent) and display them.
- FR8: Either app can trigger the phone to connect to one of those known networks (`adb shell cmd wifi connect-network <SSID>` — no new credential entry required, since the network is already saved on-device).
- FR9: Either app can toggle the phone's Wi-Fi radio on/off (`adb shell svc wifi enable/disable`).
- FR10: Connection status (SSID, signal strength, connected/disconnected) surfaced in both UIs, kept in sync.

### 5.4 Control Channel
- FR11: A persistent command channel between desktop and phone, carried over USB, handles: switch camera, mute, Wi-Fi connect/disconnect, status pings — bidirectional, so either side can initiate and both reflect the resulting state.
- FR12: Reconnect gracefully if the USB link is briefly interrupted (cable jostle, phone screen lock, etc.).

### 5.5 UI Requirements
- FR13: Both phone and desktop apps ship with a **dark theme by default**, modern/minimal visual style (clear typography, no dated widget chrome).
- FR14: Core controls (camera toggle, mic mute, Wi-Fi network list + connect, tether toggle) are present and functionally identical on both apps — a true mirrored control panel, not a desktop-only power feature with a passive phone app.

---

## 6. Non-Functional Requirements

- **Resource usage:** Desktop app idle RAM target < 150MB; camera/mic streaming active < 300MB — soft target, not a hard constraint.
- **Latency:** Camera/mic stream latency target < 200ms end-to-end (comfortably achievable over USB).
- **Reliability:** Auto-reconnect on USB link drop.
- **Security:** Wired-only for v1 — no network exposure, no pairing-over-network concerns. Trust boundary is "cable is physically plugged in."

---

## 7. Proposed Architecture

```
┌─────────────────────┐            USB Cable             ┌──────────────────────┐
│   Android App          │◄─────────────────────────────────►│   Windows Desktop App  │
│  - CameraX capture       │   (ADB transport + USB RNDIS      │  - Media client          │
│  - Audio capture          │    tethering interface for        │  - Virtual cam driver    │
│  - Media encoder            │    media/control traffic)          │  - Virtual mic driver    │
│  - Mirrored control UI        │                                    │  - ADB client              │
│  - ADB-reachable                │                                    │  - Mirrored control UI      │
│    (Wi-Fi commands)               │                                    │  - netsh (tether route)      │
└─────────────────────┘                                  └──────────────────────┘
```

**How media travels over USB:** When USB tethering is enabled on the phone, Android exposes a virtual network interface (RNDIS/NCM) to the PC over the same cable. The desktop app talks to the phone over that interface for both the camera/mic stream and the control channel. ADB itself runs over its own USB transport in parallel for Wi-Fi commands and diagnostics.

- **Media transport:** WebRTC running over the USB-provided network interface — gives adaptive handling and clean encode/decode without a custom protocol, with nothing leaving the cable.
- **Control channel:** WebRTC DataChannel (same connection), bidirectional — either app can send a command and both apps update their displayed state on any change, regardless of origin.
- **Wi-Fi control path:** ADB over USB — the only reliable path without root, and sufficient since we're only listing/triggering already-known networks rather than provisioning new ones.
- **Virtual camera/mic on Windows 11:** built via the Windows 11 Frame Server virtual camera API (Media Foundation) + a virtual audio endpoint (WASAPI virtual device, similar approach to VB-Cable). Targeting Windows 11 only removes the Windows 10 DirectShow-filter-signing complexity entirely.

---

## 8. Recommended Tech Stack

| Component | Choice | Rationale |
|---|---|---|
| Android app | Kotlin + CameraX + WebRTC (Android library) | Native, modern, no cross-platform tax since Android-only |
| Desktop app | C# / .NET (WPF or WinUI 3) | Native Win32/Media Foundation interop for virtual cam/mic; direct access to ADB/Process APIs; WinUI 3 gives a modern dark-mode-friendly design system out of the box |
| Media transport | WebRTC over the USB-provided network interface | Handles encode/decode, adaptive quality, reconnection without custom protocol work |
| Control channel | WebRTC DataChannel | Reuse existing connection; bidirectional by nature |
| Wi-Fi control | ADB (`adb shell` commands invoked via `Process` from either app's backend, phone side driving its own local state) | Only reliable path without root; no new-credential handling needed |

---

## 9. Milestones (Suggested Phasing)

1. **M1 — Control channel + Wi-Fi control over USB (no media yet).** Prove ADB integration end-to-end: list known networks, connect, toggle radio — from both a bare-bones desktop UI and phone UI. Least risky, highest-certainty win.
2. **M2 — Camera streaming.** Android → Desktop video over the USB network interface, rendered in a plain window (not yet a virtual device).
3. **M3 — Virtual camera driver integration (Windows 11 Frame Server API).** Make the received stream appear as a real webcam device to other Windows apps.
4. **M4 — Microphone streaming + virtual mic device.**
5. **M5 — Polish.** Dark-mode UI pass on both apps, reconnect logic, mirrored-state edge cases, tray app UX on desktop.

---

## 10. Open Questions

- Exact ADB command/API to enumerate a phone's *saved* Wi-Fi networks reliably across Android/MIUI versions (`cmd wifi list-networks` behavior can vary by OEM skin — worth a quick spike on the actual Redmi/MIUI build before committing to M1).
- Initial pairing/handshake flow between phone app and desktop app on first USB connection (a simple ADB-trust-based handshake is likely sufficient given the wired-only trust model).
- Conflict resolution if both apps issue a command at the same instant (e.g., phone taps "connect Wi-Fi" while desktop taps "disconnect") — likely just last-write-wins with a debounce, but worth deciding explicitly during M1.

---

## 11. Out of Scope

- Any wireless (Wi-Fi/LAN) transport between phone and PC — USB-wired only.
- Connecting the phone to networks it doesn't already know/have saved.
